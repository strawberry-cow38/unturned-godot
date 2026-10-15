//! Real Winit/X11 pointer entry, popup navigation, primary paste and text drag.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, save_to_disk},
    winit::{WinitPlugin, WinitSettings},
};
use std::{
    sync::{
        Arc,
        atomic::{AtomicU32, Ordering},
    },
    time::{Duration, Instant},
};
#[derive(Resource)]
struct Journey {
    frame: Arc<AtomicU32>,
    path: std::path::PathBuf,
    checks: Vec<Value>,
}
#[derive(Component)]
struct Status;
#[test]
#[ignore = "actual X11/Vulkan/xdotool; UG_SEARCH_POINTER_SHOT sets artifact prefix"]
fn os_popup_primary_and_selected_text_drag_keep_original_editor() {
    let frame = Arc::new(AtomicU32::new(0));
    let clock = frame.clone();
    let title = format!("Source search pointer {}", std::process::id());
    let thread_title = title.clone();
    let thread = std::thread::spawn(move || {
        use arboard::{LinuxClipboardKind, SetExtLinux};
        let mut clipboard = arboard::Clipboard::new().unwrap();
        let start = Instant::now();
        for (boundary, args) in [
            (
                8,
                vec![
                    "mousemove",
                    "--window",
                    "$window",
                    "180",
                    "145",
                    "click",
                    "1",
                    "key",
                    "ctrl+a",
                ],
            ),
            (
                12,
                vec![
                    "mousemove",
                    "--window",
                    "$window",
                    "180",
                    "145",
                    "click",
                    "3",
                ],
            ),
            (16, vec!["key", "c", "o", "Return"]),
            (
                20,
                vec![
                    "mousemove",
                    "--window",
                    "$window",
                    "500",
                    "145",
                    "click",
                    "2",
                ],
            ),
            (
                24,
                vec![
                    "mousemove",
                    "--window",
                    "$window",
                    "68",
                    "145",
                    "click",
                    "--repeat",
                    "2",
                    "--delay",
                    "100",
                    "1",
                ],
            ),
            (
                28,
                vec![
                    "mousemove",
                    "--window",
                    "$window",
                    "68",
                    "145",
                    "mousedown",
                    "1",
                    "sleep",
                    "0.08",
                    "mousemove",
                    "--window",
                    "$window",
                    "500",
                    "145",
                    "sleep",
                    "0.08",
                    "mouseup",
                    "1",
                ],
            ),
            (32, vec!["key", "ctrl+z"]),
        ] {
            while clock.load(Ordering::SeqCst) < boundary {
                assert!(start.elapsed() < Duration::from_secs(90));
                std::thread::sleep(Duration::from_millis(1));
            }
            let out = std::process::Command::new("xdotool")
                .args(["search", "--name", &thread_title])
                .output()
                .unwrap();
            let window = String::from_utf8(out.stdout).unwrap();
            let window = window.lines().last().unwrap();
            assert!(
                std::process::Command::new("xdotool")
                    .args(["windowfocus", "--sync", window])
                    .status()
                    .unwrap()
                    .success()
            );
            if boundary == 20 {
                clipboard
                    .set()
                    .clipboard(LinuxClipboardKind::Primary)
                    .text(" Oak\n\t")
                    .unwrap();
            }
            let args: Vec<_> = args
                .into_iter()
                .map(|a| if a == "$window" { window } else { a })
                .collect();
            assert!(
                std::process::Command::new("xdotool")
                    .args(args)
                    .status()
                    .unwrap()
                    .success()
            );
        }
        while clock.load(Ordering::SeqCst) < 38 {
            std::thread::sleep(Duration::from_millis(5));
        }
    });
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title,
                    resolution: (760, 420).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(WinitSettings::continuous())
    .init_resource::<crate::character::nameplate::OriginalFont>()
    .insert_resource(UiState {
        screen: Screen::Crafting,
        ..default()
    })
    .insert_resource(Menu {
        open: true,
        browse: Browse {
            search: "Birch Maple".into(),
            ..default()
        },
        ..default()
    })
    .insert_resource(Journey {
        frame,
        path: std::env::var("UG_SEARCH_POINTER_SHOT")
            .unwrap_or("/tmp/bevy-search-pointer.png".into())
            .into(),
        checks: vec![],
    })
    .add_plugins((crate::ui::input_order::InputOrderPlugin, SearchPlugin))
    .add_systems(Startup, build)
    .add_systems(Last, finish);
    app.run();
    thread.join().unwrap();
}
fn build(
    mut commands: Commands,
    mut fields: Query<(Entity, &mut EditableText, &mut History, &mut Field)>,
    font: Res<crate::character::nameplate::OriginalFont>,
) {
    commands.spawn(Camera2d);
    commands.spawn((
        Text::new("Source LineEdit pointer affordances"),
        TextFont {
            font: font.0.clone().into(),
            font_size: 26.0.into(),
            ..default()
        },
        Node {
            position_type: PositionType::Absolute,
            left: px(40.),
            top: px(40.),
            ..default()
        },
    ));
    prepare(
        &mut commands,
        &mut fields,
        "Birch Maple",
        Rect::new(40., 120., 720., 170.),
        &font.0,
        1.,
    );
    commands.spawn((
        Status,
        Text::new("Actual X11 input"),
        TextFont {
            font: font.0.clone().into(),
            font_size: 19.0.into(),
            ..default()
        },
        TextColor(Color::srgb(0.6, 0.9, 0.65)),
        Node {
            position_type: PositionType::Absolute,
            left: px(40.),
            top: px(210.),
            ..default()
        },
    ));
}
fn finish(
    mut journey: ResMut<Journey>,
    fields: Query<(Entity, &EditableText), With<Field>>,
    menu: Res<Menu>,
    popup: Res<popup::Popup>,
    mut commands: Commands,
    mut labels: Query<&mut Text, With<Status>>,
    mut exit: MessageWriter<AppExit>,
) {
    let frame = journey.frame.fetch_add(1, Ordering::SeqCst) + 1;
    let stage = match frame {
        10 => Some(("selected", "Birch Maple", true)),
        14 => Some(("popup", "Birch Maple", true)),
        18 => Some(("copied", "Birch Maple", true)),
        22 => Some(("primary", "Birch Maple Oak", false)),
        26 => Some(("word", "Birch Maple Oak", true)),
        30 => Some(("dragged", " Maple OakBirch", true)),
        34 => Some(("undo", "Birch Maple Oak", false)),
        _ => None,
    };
    if let Some((stage, expected, selection)) = stage {
        let (entity, text) = fields.single().unwrap();
        let value = text.value().to_string();
        journey.checks.push(json!({"stage":stage,"frame":frame,"field":entity.to_bits(),"value":value,"selected":text.editor.selected_text(),"popup":popup.typing(),"browse":menu.browse.search}));
        std::fs::write(
            journey.path.with_extension("json"),
            serde_json::to_vec_pretty(&journey.checks).unwrap(),
        )
        .unwrap();
        assert_eq!(value, expected, "OS stage {stage}");
        assert_eq!(
            !text.editor.raw_selection().is_collapsed(),
            selection,
            "OS selection {stage}"
        );
        assert_eq!(menu.browse.search, expected);
        for mut label in &mut labels {
            label.0 = format!(
                "{stage}: {value}\nOriginal field retained; accepted text edits update Browse"
            );
        }
    }

    let capture = match frame {
        15 => Some("popup"),
        23 => Some("primary"),
        31 => Some("dragged"),
        35 => Some("undo"),
        _ => None,
    };
    if let Some(stage) = capture {
        let stem = journey.path.file_stem().unwrap().to_string_lossy();
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(
                journey.path.with_file_name(format!("{stem}-{stage}.png")),
            ));
    }
    if frame >= 38 {
        assert_eq!(journey.checks.len(), 7);
        let field = journey.checks[0]["field"].clone();
        assert!(journey.checks.iter().all(|c| c["field"] == field));
        exit.write(AppExit::Success);
    }
    std::thread::sleep(Duration::from_millis(200));
}
