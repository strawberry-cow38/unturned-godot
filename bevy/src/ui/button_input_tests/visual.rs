//! Real X11 input through Winit, picking and the production UI action observer.
//! The app pauses500ms in Last so cursor movement and a12ms click are batched.
use super::*;
use bevy::{
    app::AppExit,
    picking::pointer::{PointerAction, PointerInput},
    render::view::screenshot::{Screenshot, save_to_disk},
    winit::{WinitPlugin, WinitSettings},
};
use std::{
    sync::{
        Arc, Mutex,
        atomic::{AtomicU32, Ordering},
    },
    time::{Duration, Instant},
};

#[derive(Resource)]
struct Fixture {
    frame: Arc<AtomicU32>,
    shot: std::path::PathBuf,
    edges: Vec<serde_json::Value>,
    intents: Vec<serde_json::Value>,
    clicks: Arc<Mutex<Vec<serde_json::Value>>>,
    selection: Option<(u8, u8, u8)>,
}
#[derive(Component)]
struct Status;

#[test]
#[ignore = "requires Vulkan/X11/xdotool; UG_BUTTON_INPUT_SHOT sets PNG path"]
fn os_twelve_ms_clicks_at_two_fps_select_equip_and_respawn() {
    let frame = Arc::new(AtomicU32::new(0));
    let title = format!("Native fast button input {}", std::process::id());
    let clicks = Arc::new(Mutex::new(Vec::new()));
    let click_frame = frame.clone();
    let click_title = title.clone();
    let click_times = clicks.clone();
    let thread = std::thread::spawn(move || {
        let start = Instant::now();
        for (target_frame, y) in [(8, 110), (10, 180), (12, 250)] {
            while click_frame.load(Ordering::SeqCst) < target_frame {
                assert!(
                    start.elapsed() < Duration::from_secs(60),
                    "renderer did not reach click boundary"
                );
                std::thread::sleep(Duration::from_millis(1));
            }
            let window = std::process::Command::new("xdotool")
                .args(["search", "--name", &click_title])
                .output()
                .unwrap();
            assert!(window.status.success());
            let window = String::from_utf8(window.stdout).unwrap();
            let window = window.lines().last().unwrap();
            let began = Instant::now();
            let status = std::process::Command::new("xdotool")
                .args([
                    "mousemove",
                    "--window",
                    window,
                    "220",
                    &y.to_string(),
                    "mousedown",
                    "1",
                    "sleep",
                    "0.012",
                    "mouseup",
                    "1",
                ])
                .status()
                .unwrap();
            assert!(status.success());
            click_times.lock().unwrap().push(json!({"boundaryFrame":target_frame,"y":y,"requestedHoldMs":12,"commandMs":began.elapsed().as_secs_f64()*1000.}));
        }
    });
    let shot = std::env::var("UG_BUTTON_INPUT_SHOT")
        .unwrap_or_else(|_| "/tmp/bevy-fast-buttons.png".into());
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (520, 430).into(),
                    title,
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
    .init_resource::<Settings>()
    .init_resource::<UiModel>()
    .init_resource::<UiState>()
    .init_resource::<Activations>()
    .add_message::<UiIntent>()
    .insert_resource(crate::LaunchOptions(crate::cli::Options::default()))
    .insert_resource(Fixture {
        frame,
        shot: shot.into(),
        edges: Vec::new(),
        intents: Vec::new(),
        clicks,
        selection: None,
    })
    .add_observer(activate_button)
    .add_observer(|_: On<Activate>, mut count: ResMut<Activations>| count.0 += 1)
    .add_systems(Startup, build)
    .add_systems(Update, record)
    .add_systems(Last, finish);
    app.run();
    thread.join().unwrap();
}
fn build(mut commands: Commands) {
    commands.spawn(Camera2d);
    commands.spawn((
        Text::new("12 ms clicks / 2 FPS"),
        TextFont {
            font_size: 26.0.into(),
            ..default()
        },
        Node {
            position_type: PositionType::Absolute,
            left: Val::Px(40.),
            top: Val::Px(25.),
            ..default()
        },
    ));
    for (i, label, action) in [
        (
            0,
            "Select inventory cell (2,3,4)",
            UiButton::Select(2, 3, 4),
        ),
        (1, "Equip selected item", UiButton::Equip),
        (2, "Respawn", UiButton::Respawn),
    ] {
        commands
            .spawn((
                Button,
                action,
                Node {
                    position_type: PositionType::Absolute,
                    left: Val::Px(40.),
                    top: Val::Px(80. + i as f32 * 70.),
                    width: Val::Px(360.),
                    height: Val::Px(60.),
                    justify_content: JustifyContent::Center,
                    align_items: AlignItems::Center,
                    ..default()
                },
                BackgroundColor(Color::srgb(0.18, 0.25, 0.31)),
            ))
            .with_child((
                Text::new(label),
                TextFont {
                    font_size: 20.0.into(),
                    ..default()
                },
            ));
    }
    commands.spawn((
        Status,
        Text::new("Waiting for real mouse input"),
        TextFont {
            font_size: 19.0.into(),
            ..default()
        },
        TextColor(Color::srgb(0.5, 1., 0.6)),
        Node {
            position_type: PositionType::Absolute,
            left: Val::Px(40.),
            top: Val::Px(310.),
            ..default()
        },
    ));
}
fn record(
    mut fixture: ResMut<Fixture>,
    mut inputs: MessageReader<PointerInput>,
    mut intents: MessageReader<UiIntent>,
    state: Res<UiState>,
    count: Res<Activations>,
    mut text: Query<&mut Text, With<Status>>,
) {
    let (mut press, mut release, mut movement) = (0, 0, 0);
    for input in inputs.read() {
        match input.action {
            PointerAction::Press(_) => press += 1,
            PointerAction::Release(_) => release += 1,
            PointerAction::Move { .. } => movement += 1,
            _ => {}
        }
    }
    if press + release + movement > 0 {
        let frame = fixture.frame.load(Ordering::SeqCst);
        fixture
            .edges
            .push(json!({"frame":frame,"press":press,"release":release,"move":movement}));
    }
    for intent in intents.read() {
        if let UiIntent::Action { name, args } = intent {
            fixture.intents.push(json!({"name":name,"args":args}));
        }
    }
    if state.selected_item.is_some() {
        fixture.selection = state.selected_item;
    }
    for mut text in &mut text {
        *text = Text::new(format!(
            "Selected cell: {:?}\nUI activations: {}\nEquip / Respawn intents: {}",
            fixture.selection,
            count.0,
            fixture.intents.len()
        ));
    }
}
fn finish(
    fixture: Res<Fixture>,
    count: Res<Activations>,
    mut commands: Commands,
    mut exit: MessageWriter<AppExit>,
) {
    let frame = fixture.frame.fetch_add(1, Ordering::SeqCst) + 1;
    if frame == 16 {
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(fixture.shot.clone()));
    }
    if frame >= 22 {
        std::fs::write(fixture.shot.with_extension("json"),serde_json::to_vec_pretty(&json!({
            "input":"actual xdotool through Winit/Bevy picking","framePauseMs":500,
            "clicks":*fixture.clicks.lock().unwrap(),"pointerFrames":fixture.edges,"intents":fixture.intents,
            "selectedCell":fixture.selection,"activations":count.0
        })).unwrap()).unwrap();
        assert_eq!(count.0, 3);
        assert_eq!(fixture.selection, Some((2, 3, 4)));
        assert_eq!(
            fixture.intents,
            vec![
                json!({"name":"equipAndHold","args":{"page":2,"x":3,"y":4}}),
                json!({"name":"respawn","args":{}})
            ]
        );
        assert_eq!(
            fixture
                .edges
                .iter()
                .filter(|frame| frame["press"] == 1
                    && frame["release"] == 1
                    && frame["move"].as_u64().unwrap() > 0)
                .count(),
            3,
            "all real clicks must move, press and release in the same observed input frame"
        );
        exit.write(AppExit::Success);
    }
    std::thread::sleep(Duration::from_millis(500));
}
