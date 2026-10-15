//! Actual X11 keyboard delivery through Winit at a deliberately slow frame rate.
//! The original production regression was a complete Ctrl+A chord in one frame.
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
struct Fixture {
    frame: Arc<AtomicU32>,
    shot: std::path::PathBuf,
    inputs: Vec<serde_json::Value>,
    checks: Vec<serde_json::Value>,
}
#[derive(Component)]
struct Status;

#[test]
#[ignore = "requires Vulkan/X11/xdotool; UG_KEYBOARD_INPUT_SHOT sets PNG prefix"]
fn os_twelve_ms_modifier_chords_at_two_fps_select_move_and_undo() {
    let frame = Arc::new(AtomicU32::new(0));
    let title = format!("Native focused keyboard {}", std::process::id());
    let clock = frame.clone();
    let thread_title = title.clone();
    let thread = std::thread::spawn(move || {
        let start = Instant::now();
        for (boundary, args) in [
            (
                8,
                vec![
                    "mousemove",
                    "--window",
                    "$window",
                    "250",
                    "145",
                    "mousedown",
                    "1",
                    "sleep",
                    "0.012",
                    "mouseup",
                    "1",
                ],
            ),
            (
                10,
                vec![
                    "keydown", "ctrl", "keydown", "a", "sleep", "0.012", "keyup", "a", "keyup",
                    "ctrl",
                ],
            ),
            (14, vec!["type", "--delay", "12", "Maple"]),
            (
                18,
                vec![
                    "keydown", "shift", "keydown", "Left", "sleep", "0.012", "keyup", "Left",
                    "keyup", "shift",
                ],
            ),
            (22, vec!["type", "--delay", "12", "X"]),
            (
                26,
                vec![
                    "keydown", "ctrl", "keydown", "z", "sleep", "0.012", "keyup", "z", "keyup",
                    "ctrl",
                ],
            ),
            (
                30,
                vec![
                    "keydown", "ctrl", "keydown", "shift", "keydown", "z", "sleep", "0.012",
                    "keyup", "z", "keyup", "shift", "keyup", "ctrl",
                ],
            ),
        ] {
            while clock.load(Ordering::SeqCst) < boundary {
                assert!(
                    start.elapsed() < Duration::from_secs(90),
                    "renderer did not reach OS input boundary"
                );
                std::thread::sleep(Duration::from_millis(1));
            }
            let output = std::process::Command::new("xdotool")
                .args(["search", "--name", &thread_title])
                .output()
                .unwrap();
            assert!(output.status.success());
            let window = String::from_utf8(output.stdout).unwrap();
            let window = window.lines().last().unwrap();
            // A bare Xvfb server has no window manager to transfer keyboard
            // focus when the pointer clicks; target the owned Winit window.
            assert!(
                std::process::Command::new("xdotool")
                    .args(["windowfocus", "--sync", window])
                    .status()
                    .unwrap()
                    .success()
            );
            let args: Vec<_> = args
                .into_iter()
                .map(|arg| if arg == "$window" { window } else { arg })
                .collect();
            assert!(
                std::process::Command::new("xdotool")
                    .args(args)
                    .status()
                    .unwrap()
                    .success()
            );
        }
    });
    let shot = std::env::var("UG_KEYBOARD_INPUT_SHOT")
        .unwrap_or_else(|_| "/tmp/bevy-fast-keyboard.png".into());
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
            search: "Birch".into(),
            ..default()
        },
        ..default()
    })
    .insert_resource(Fixture {
        frame,
        shot: shot.into(),
        inputs: vec![],
        checks: vec![],
    })
    .add_plugins(SearchPlugin)
    .add_systems(Startup, build)
    .add_systems(Update, record)
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
        Text::new("12 ms keyboard chords / 2 FPS"),
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
        "Birch",
        Rect::from_corners(Vec2::new(40., 120.), Vec2::new(720., 170.)),
        &font.0,
        1.,
    );
    commands.spawn((
        Status,
        Text::new("Waiting for actual OS input"),
        TextFont {
            font: font.0.clone().into(),
            font_size: 19.0.into(),
            ..default()
        },
        TextColor(Color::srgb(0.6, 0.9, 0.65)),
        Node {
            position_type: PositionType::Absolute,
            left: px(40.),
            top: px(205.),
            ..default()
        },
    ));
}
fn record(mut fixture: ResMut<Fixture>, mut inputs: MessageReader<KeyboardInput>) {
    let frame = fixture.frame.load(Ordering::SeqCst) + 1;
    let batch: Vec<_> = inputs
        .read()
        .map(|input| {
            json!({
                "key":format!("{:?}", input.logical_key),"code":format!("{:?}", input.key_code),
                "pressed":input.state.is_pressed(),"repeat":input.repeat,"text":input.text,
            })
        })
        .collect();
    if !batch.is_empty() {
        fixture.inputs.push(json!({"frame":frame,"events":batch}));
    }
}
fn finish(
    mut fixture: ResMut<Fixture>,
    fields: Query<&EditableText, With<Field>>,
    menu: Res<Menu>,
    mut labels: Query<&mut Text, With<Status>>,
    mut commands: Commands,
    mut exit: MessageWriter<AppExit>,
) {
    let frame = fixture.frame.fetch_add(1, Ordering::SeqCst) + 1;
    let stage = match frame {
        12 => Some(("select", "Birch", true)),
        16 => Some(("replace", "Maple", false)),
        20 => Some(("caret", "Maple", true)),
        24 => Some(("replace-selection", "MaplX", false)),
        28 => Some(("undo", "Maple", false)),
        32 => Some(("redo", "MaplX", false)),
        _ => None,
    };
    if let Some((stage, expected, selected)) = stage {
        let text = fields.single().unwrap();
        let value = text.value().to_string();
        let selection = text.editor.raw_selection();
        fixture
            .checks
            .push(json!({"stage":stage,"frame":frame,"value":value,
            "browse":menu.browse.search,"selected":!selection.is_collapsed(),
            "anchor":selection.anchor().index(),"caret":selection.focus().index()}));
        // Save diagnostics before assertions so a negative control is reviewable.
        std::fs::write(fixture.shot.with_extension("json"), serde_json::to_vec_pretty(&json!({
            "input":"actual xdotool through Winit, InputSystems and focused dispatch", "framePauseMs":500,
            "requestedChordHoldMs":12,"checks":fixture.checks,"keyboardFrames":fixture.inputs,
        })).unwrap()).unwrap();
        assert_eq!(value, expected, "actual OS stage {stage}");
        assert_eq!(
            menu.browse.search, expected,
            "accepted editor change must update Browse"
        );
        assert_eq!(
            !selection.is_collapsed(),
            selected,
            "actual OS selection stage {stage}"
        );
        for mut label in &mut labels {
            label.0 = fixture
                .checks
                .iter()
                .map(|check| {
                    format!(
                        "{}: {}  (selection {})",
                        check["stage"].as_str().unwrap(),
                        check["value"].as_str().unwrap(),
                        check["selected"]
                    )
                })
                .collect::<Vec<_>>()
                .join("\n");
        }
        if matches!(stage, "select" | "caret" | "undo" | "redo") {
            let stem = fixture.shot.file_stem().unwrap().to_string_lossy();
            let shot = fixture.shot.with_file_name(format!("{stem}-{stage}.png"));
            commands
                .spawn(Screenshot::primary_window())
                .observe(save_to_disk(shot));
        }
    }
    if frame >= 36 {
        let chords = fixture
            .inputs
            .iter()
            .filter(|batch| {
                let events = batch["events"].as_array().unwrap();
                events
                    .iter()
                    .any(|e| e["key"] == "Control" && e["pressed"] == true)
                    && events
                        .iter()
                        .any(|e| e["key"] == "Control" && e["pressed"] == false)
            })
            .count();
        assert_eq!(
            chords, 3,
            "Ctrl+A/Undo/Redo must each arrive as a complete same-frame chord"
        );
        assert_eq!(fixture.checks.len(), 6);
        exit.write(AppExit::Success);
    }
    std::thread::sleep(Duration::from_millis(500));
}
