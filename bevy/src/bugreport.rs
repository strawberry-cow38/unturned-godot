//! game/BugReporter.cs, native half: hold the BugReport binding to capture a
//! screenshot at press, the microphone while held and a second screenshot at
//! release; console `report <text>` files a typed report. The bridge's
//! BugReportOutbox (BugReportCore) writes the outbox and uploads it.
use crate::{
    bridge::CoreBridge,
    settings::{GameAction, Settings},
    ui::{Screen, UiIntent, UiModel, UiState},
};
use bevy::{
    prelude::*,
    render::view::screenshot::{Screenshot, ScreenshotCaptured},
    window::WindowFocused,
};
use serde_json::{Value, json};
use std::{
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};

pub const MAX_SECONDS: f64 = 60.;
pub const TAP_SECONDS: f64 = 0.4;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ReportState {
    #[default]
    Idle,
    Recording,
    Finalizing,
}
/// BugReportCore.ReportSession, the press/hold/release machine.
#[derive(Clone, Debug, Default)]
pub struct ReportSession {
    pub state: ReportState,
    pub elapsed: f64,
    pub was_tap: bool,
    pub hit_timeout: bool,
}
impl ReportSession {
    /// True when this press starts a recording (never while one is live).
    pub fn press(&mut self) -> bool {
        if self.state != ReportState::Idle {
            return false;
        }
        *self = Self { state: ReportState::Recording, ..default() };
        true
    }
    /// True when the 60 s cap forces a finalize.
    pub fn tick(&mut self, dt: f64) -> bool {
        if self.state != ReportState::Recording {
            return false;
        }
        self.elapsed += dt;
        if self.elapsed < MAX_SECONDS {
            return false;
        }
        self.hit_timeout = true;
        self.state = ReportState::Finalizing;
        true
    }
    /// Release, focus loss or alt-tab; a short hold is a screenshot-only report.
    pub fn release(&mut self) -> bool {
        if self.state != ReportState::Recording {
            return false;
        }
        self.was_tap = self.elapsed < TAP_SECONDS;
        self.state = ReportState::Finalizing;
        true
    }
    pub fn done(&mut self) {
        self.state = ReportState::Idle;
        self.elapsed = 0.;
    }
}

/// Where reports wait for upload (user://bugreports/outbox in the source).
pub fn outbox_root() -> PathBuf {
    #[cfg(test)]
    return Settings::path().parent().unwrap_or(Path::new(".")).join("bugreports").join("outbox");
    #[allow(unreachable_code)]
    if let Some(path) = std::env::var_os("UG_BEVY_BUGREPORTS") {
        return PathBuf::from(path).join("outbox");
    }
    Settings::path()
        .parent()
        .unwrap_or(Path::new("."))
        .join("bugreports")
        .join("outbox")
}

/// Endpoint and key: UG_BUGREPORT_KEY first, then bugreport.cfg [report].
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Config {
    pub endpoint: String,
    pub key: String,
    pub upload: bool,
}
impl Config {
    pub fn load() -> Self {
        let path = Settings::path().parent().unwrap_or(Path::new(".")).join("bugreport.cfg");
        let text = std::fs::read_to_string(path).unwrap_or_default();
        Self::parse(&text, std::env::var("UG_BUGREPORT_KEY").ok().as_deref())
    }
    pub fn parse(cfg: &str, env_key: Option<&str>) -> Self {
        let mut section = "";
        let mut endpoint = String::new();
        let mut key = String::new();
        for line in cfg.lines().map(str::trim) {
            if line.starts_with('[') && line.ends_with(']') {
                section = &line[1..line.len() - 1];
            } else if section == "report" {
                if let Some((k, v)) = line.split_once('=') {
                    let v = v.trim().trim_matches('"').to_owned();
                    match k.trim() {
                        "endpoint" => endpoint = v,
                        "key" => key = v.trim().to_owned(),
                        _ => {}
                    }
                }
            }
        }
        // The launcher's child-process variable wins over the file.
        if let Some(env) = env_key.map(str::trim).filter(|k| !k.is_empty()) {
            key = env.to_owned();
        }
        Self {
            endpoint,
            key,
            upload: std::env::var("UG_BUGREPORT_UPLOAD").as_deref() != Ok("0"),
        }
    }
}

struct Capture {
    samples: Arc<Mutex<Vec<f32>>>,
    rate: u32,
    _stream: cpal::Stream,
}
/// Microphone capture; a missing device yields no frames (classified no_mic).
fn start_mic() -> Result<Option<Capture>, String> {
    use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
    let host = cpal::default_host();
    let Some(device) = host.default_input_device() else {
        return Ok(None);
    };
    let config = device.default_input_config().map_err(|e| e.to_string())?;
    let rate = config.sample_rate();
    let channels = config.channels().max(1) as usize;
    let cap = (MAX_SECONDS as usize * rate as usize) + 1024;
    let samples = Arc::new(Mutex::new(Vec::<f32>::with_capacity(rate as usize * 4)));
    let sink = samples.clone();
    let push = move |frames: &mut dyn Iterator<Item = f32>| {
        let Ok(mut out) = sink.lock() else { return };
        let mut frame = Vec::with_capacity(channels);
        for s in frames {
            frame.push(s);
            if frame.len() == channels {
                if out.len() < cap {
                    out.push(frame.iter().sum::<f32>() / channels as f32);
                }
                frame.clear();
            }
        }
    };
    let err = |e| warn!("bug report microphone: {e}");
    let stream = match config.sample_format() {
        cpal::SampleFormat::F32 => {
            let mut push = push;
            device.build_input_stream(&config.into(), move |d: &[f32], _: &_| push(&mut d.iter().copied()), err, None)
        }
        cpal::SampleFormat::I16 => {
            let mut push = push;
            device.build_input_stream(&config.into(), move |d: &[i16], _: &_| push(&mut d.iter().map(|s| *s as f32 / 32768.)), err, None)
        }
        cpal::SampleFormat::U16 => {
            let mut push = push;
            device.build_input_stream(&config.into(), move |d: &[u16], _: &_| push(&mut d.iter().map(|s| (*s as f32 - 32768.) / 32768.)), err, None)
        }
        other => return Err(format!("unsupported microphone format {other:?}")),
    }
    .map_err(|e| e.to_string())?;
    stream.play().map_err(|e| e.to_string())?;
    Ok(Some(Capture { samples, rate, _stream: stream }))
}

#[derive(Default)]
struct Shot {
    path: PathBuf,
    done: Arc<AtomicBool>,
}
struct Finish {
    before: Option<Shot>,
    after: Option<Shot>,
    typed: Option<String>,
    pcm: Option<PathBuf>,
    rate: u32,
    audio: &'static str,
    waited: f32,
    session: ReportSession,
}

#[derive(Resource)]
pub struct BugReporter {
    pub session: ReportSession,
    pub config: Config,
    toast: Option<(String, f32)>,
    before: Option<Shot>,
    audio: &'static str,
    finishing: Option<Finish>,
    held: bool,
    worst_ms: f32,
    worst_left: f32,
    polling: f32,
    retried: bool,
    staging: PathBuf,
    count: u64,
    /// Shown for the capture sidecar / tests.
    pub last_dir: Option<String>,
}
impl Default for BugReporter {
    fn default() -> Self {
        Self {
            session: default(),
            config: Config::load(),
            toast: None,
            before: None,
            audio: "disabled",
            finishing: None,
            held: false,
            worst_ms: 0.,
            worst_left: 0.,
            polling: 0.,
            retried: false,
            // Beside the outbox (user data), never the shared system temp dir.
            staging: outbox_root().parent().unwrap_or(Path::new(".")).join("staging"),
            count: 0,
            last_dir: None,
        }
    }
}

#[derive(Component)]
struct Overlay;

pub struct BugReportPlugin;
impl Plugin for BugReportPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<BugReporter>()
            .add_systems(Startup, spawn_overlay)
            .add_systems(Update, (input, finish, poll, overlay).chain().after(crate::ui::UiUpdate));
    }
}

fn spawn_overlay(mut commands: Commands) {
    commands.spawn((
        Overlay,
        Text::new(""),
        TextColor(Color::srgb(1., 0.3, 0.3)),
        Node {
            position_type: PositionType::Absolute,
            top: px(18. * 0.5),
            left: percent(50),
            margin: UiRect::left(px(-60.)),
            width: px(120.),
            justify_content: JustifyContent::Center,
            ..default()
        },
        TextLayout::justify(Justify::Center),
        // Source CanvasLayer 260: a recording indicator is never occluded.
        GlobalZIndex(260),
        Visibility::Hidden,
    ));
}

fn shot(commands: &mut Commands, path: PathBuf) -> Shot {
    let done = Arc::new(AtomicBool::new(false));
    let flag = done.clone();
    let target = path.clone();
    commands.spawn(Screenshot::primary_window()).observe(move |captured: On<ScreenshotCaptured>| {
        match captured.image.clone().try_into_dynamic() {
            Ok(image) => {
                if let Err(e) = image.to_rgb8().save(&target) {
                    warn!("bug report screenshot failed: {e}");
                }
            }
            Err(e) => warn!("bug report screenshot failed: {e}"),
        }
        flag.store(true, Ordering::Release);
    });
    Shot { path, done }
}

/// LineEdit focus: a report key typed into a field is text, not a report.
fn typing(state: &UiState) -> bool {
    matches!(state.screen, Screen::Console | Screen::Chat)
        // KeybindMenu swallows the next press while capturing.
        || state.capturing.is_some()
        || state.menu.sp.seed_focus
        || state.network_menu.focus.is_some()
}

#[allow(clippy::too_many_arguments)]
fn input(
    mut commands: Commands,
    mut reporter: ResMut<BugReporter>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    state: Res<UiState>,
    time: Res<Time<Real>>,
    mut focus: MessageReader<WindowFocused>,
    mut intents: MessageReader<UiIntent>,
    mut model: ResMut<UiModel>,
    mut mic: Local<Option<Capture>>,
) {
    let r = &mut *reporter;
    let delta = time.delta_secs();
    let ms = delta * 1000.;
    if ms > r.worst_ms {
        r.worst_ms = ms;
        r.worst_left = 5.;
    } else {
        r.worst_left -= delta;
        if r.worst_left <= 0. {
            r.worst_ms = ms;
            r.worst_left = 5.;
        }
    }
    for intent in intents.read() {
        let UiIntent::Action { name, args } = intent else { continue };
        let text = args["text"].as_str().unwrap_or("");
        if name != "console" || !is_console_command(text) {
            continue;
        }
        let arg = text.trim()[6..].trim();
        if arg.is_empty() {
            model.notice("usage: report <what went wrong>");
        } else if r.session.state != ReportState::Idle || r.finishing.is_some() {
            r.toast = Some(("finish the current report first".into(), 3.));
            model.notice("could not write the report");
        } else {
            r.count += 1;
            let path = r.staging.join(format!("typed-{}.png", r.count));
            private_dir(&r.staging);
            r.finishing = Some(Finish {
                before: Some(shot(&mut commands, path)),
                after: None,
                typed: Some(arg.to_owned()),
                pcm: None,
                rate: 0,
                audio: "typed",
                waited: 0.,
                session: default(),
            });
            model.notice("report queued");
        }
    }
    let binding = settings.bindings.get(&GameAction::BugReport);
    let down = binding.is_some_and(|b| b.pressed(&keys, &mouse));
    let modifier = keys.any_pressed([
        KeyCode::ControlLeft,
        KeyCode::ControlRight,
        KeyCode::AltLeft,
        KeyCode::AltRight,
        KeyCode::SuperLeft,
        KeyCode::SuperRight,
    ]);
    let pressed = down && !r.held;
    let released = !down && r.held;
    r.held = down;
    let lost_focus = focus.read().any(|f| !f.focused);
    if pressed && !modifier && !typing(&state) && r.finishing.is_none() && r.session.press() {
        // Hide the overlay BEFORE the before-shot (no stale toast in the image).
        r.toast = None;
        r.count += 1;
        private_dir(&r.staging);
        r.before = Some(shot(&mut commands, r.staging.join(format!("before-{}.png", r.count))));
        *mic = None;
        r.audio = match start_mic() {
            Ok(Some(capture)) => {
                *mic = Some(capture);
                "ok"
            }
            // No input device: frames never arrive, the verdict is no_mic.
            Ok(None) => "ok",
            Err(e) => {
                warn!("bug report microphone unavailable: {e}");
                "error"
            }
        };
    }
    let mut end = (released || lost_focus) && r.session.release();
    // Elapsed in real seconds, independent of any simulation time scale.
    if r.session.tick(f64::from(delta)) {
        end = true;
    }
    if end {
        let capture = mic.take();
        let (pcm, rate) = match capture {
            Some(c) => {
                let path = r.staging.join(format!("audio-{}.pcm", r.count));
                let samples = c.samples.lock().map(|s| s.clone()).unwrap_or_default();
                let bytes: Vec<u8> = samples.iter().flat_map(|s| s.to_le_bytes()).collect();
                let ok = std::fs::write(&path, bytes).is_ok();
                (ok.then_some(path), c.rate)
            }
            None => (None, 44100),
        };
        r.finishing = Some(Finish {
            before: r.before.take(),
            after: Some(shot(&mut commands, r.staging.join(format!("after-{}.png", r.count)))),
            typed: None,
            pcm,
            rate,
            audio: r.audio,
            waited: 0.,
            session: r.session.clone(),
        });
    }
}

/// Screenshots and raw microphone PCM are private until the outbox owns them.
fn private_dir(dir: &Path) {
    let _ = std::fs::create_dir_all(dir);
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(dir, std::fs::Permissions::from_mode(0o700));
    }
}
pub fn is_console_command(text: &str) -> bool {
    let t = text.trim_start();
    // Byte-safe: console text may hold any UTF-8 at byte six.
    t.get(..6).is_some_and(|p| p.eq_ignore_ascii_case("report"))
        && t.as_bytes().get(6).is_none_or(|b| *b == b' ')
}

/// Once both shots are on disk (or a frame budget passes), hand the files to
/// the outbox; the session returns to Idle even on failure (source finally).
#[allow(clippy::too_many_arguments)]
fn finish(
    mut reporter: ResMut<BugReporter>,
    bridge: Option<Res<CoreBridge>>,
    time: Res<Time<Real>>,
    model: Res<UiModel>,
    snapshot: Option<Res<crate::player::GameSnapshot>>,
    config: Option<Res<crate::world::WorldConfig>>,
    adapter: Option<Res<bevy::render::renderer::RenderAdapterInfo>>,
    bodies: Query<&Transform, With<crate::player::PlayerBody>>,
) {
    let r = &mut *reporter;
    let Some(finish) = r.finishing.as_mut() else { return };
    finish.waited += time.delta_secs();
    let ready = |s: &Option<Shot>| s.as_ref().is_none_or(|s| s.done.load(Ordering::Acquire));
    if !(ready(&finish.before) && ready(&finish.after)) && finish.waited < 3. {
        return;
    }
    let finish = r.finishing.take().unwrap();
    let path = |s: &Option<Shot>| {
        s.as_ref()
            .filter(|s| s.path.is_file())
            .map(|s| s.path.to_string_lossy().into_owned())
    };
    let player = snapshot.as_ref().map_or(Value::Null, |s| s.0["player"].clone());
    let position = bodies.iter().next().map(|t| t.translation.to_array());
    let mut player_ctx = json!({});
    if model.connected {
        player_ctx = json!({
            "health": player["hp"], "food": player["food"], "water": player["water"],
            "stamina": player["stamina"], "infection": player["infection"],
            "position": position, "equipped": player["heldItemId"],
        });
    }
    let context = json!({
        "godot": format!("Bevy 0.20 native ({})", env!("CARGO_PKG_VERSION")),
        "os": match std::env::consts::OS { "linux" => "Linux", "windows" => "Windows", "macos" => "macOS", o => o },
        "gpu": adapter.as_ref().map(|a| a.name.clone()).unwrap_or_default(),
        "uptime_s": time.elapsed_secs_f64(),
        "map": if model.connected { config.as_ref().map(|c| c.map_name.clone()).unwrap_or_default() } else { String::new() },
        "player": player_ctx,
        "perf": {
            "fps": if time.delta_secs() > 0. { 1. / time.delta_secs() } else { 0. },
            "frame_ms": time.delta_secs() * 1000.,
            "worst_frame_ms_5s": r.worst_ms,
        },
    });
    let request = json!({
        "root": outbox_root(), "context": context,
        "shot": path(&finish.before), "shotAfter": path(&finish.after),
        "pcm": finish.pcm, "mixRate": finish.rate,
        "audioStatus": finish.audio, "typed": finish.typed,
        "wasTap": finish.session.was_tap, "hitTimeout": finish.session.hit_timeout,
        "upload": r.config.upload, "endpoint": r.config.endpoint, "key": r.config.key,
        "projectDir": Path::new(env!("CARGO_MANIFEST_DIR")).parent(),
    });
    let reply = bridge.map(|b| b.request("bugReport", request));
    match reply {
        Some(Ok(reply)) => {
            let toast = reply["result"]["toast"].as_str().unwrap_or("report failed \u{2014} see the log");
            r.toast = Some((toast.into(), 3.));
            r.last_dir = reply["result"]["dir"].as_str().map(str::to_owned);
            r.polling = 60.;
        }
        Some(Err(e)) => {
            error!("bug report failed: {e}");
            r.toast = Some(("report failed \u{2014} see the log".into(), 3.));
        }
        None => r.toast = Some(("report failed \u{2014} see the log".into(), 3.)),
    }
    if finish.typed.is_none() {
        r.session.done();
    }
    for file in [path(&finish.before), path(&finish.after)].into_iter().flatten() {
        let _ = std::fs::remove_file(file);
    }
    if let Some(pcm) = finish.pcm {
        let _ = std::fs::remove_file(pcm);
    }
}

/// RetryOutbox once per launch, then drain upload toasts while sends run.
fn poll(mut reporter: ResMut<BugReporter>, bridge: Option<Res<CoreBridge>>, time: Res<Time<Real>>, mut tick: Local<f32>) {
    let Some(bridge) = bridge else { return };
    let r = &mut *reporter;
    if !r.retried {
        r.retried = true;
        if r.config.upload {
            let _ = bridge.request("bugReportRetry", json!({"root":outbox_root(),"endpoint":r.config.endpoint,"key":r.config.key}));
            r.polling = 60.;
        }
    }
    if r.polling <= 0. {
        return;
    }
    r.polling -= time.delta_secs();
    *tick += time.delta_secs();
    if *tick < 0.5 {
        return;
    }
    *tick = 0.;
    if let Ok(reply) = bridge.request("bugReportPoll", json!({})) {
        if let Some(last) = reply["result"]["toasts"].as_array().and_then(|t| t.last()) {
            r.toast = last.as_str().map(|t| (t.to_owned(), 3.));
        }
        if reply["result"]["busy"] == false {
            r.polling = r.polling.min(1.);
        }
    }
}

fn overlay(
    mut reporter: ResMut<BugReporter>,
    time: Res<Time<Real>>,
    mut label: Query<(&mut Text, &mut Visibility, &mut Node, &mut TextFont), With<Overlay>>,
    windows: Query<&Window, With<bevy::window::PrimaryWindow>>,
    font: Option<Res<crate::character::nameplate::OriginalFont>>,
) {
    let r = &mut *reporter;
    let Ok((mut text, mut visibility, mut node, mut text_font)) = label.single_mut() else { return };
    // Label: CenterTop, Position (-120, 18), 240x24, default 16 px, under the
    // project's 2560x1440 canvas stretch.
    let k = windows.single().map_or(0.5, |w| crate::ui::godot::stretch(Vec2::new(w.width(), w.height())));
    if node.top != px(18. * k) {
        node.top = px(18. * k);
        node.margin = UiRect::left(px(-120. * k));
        node.width = px(240. * k);
        text_font.font_size = bevy::text::FontSize::Px(16. * k);
        if let Some(font) = font.as_ref() {
            text_font.font = font.0.clone().into();
        }
    }
    let (value, show) = if r.session.state == ReportState::Recording {
        let s = r.session.elapsed as u64;
        (format!("\u{25cf} REC {}:{:02}", s / 60, s % 60), true)
    } else if r.finishing.is_some() {
        // Hidden while the after-shot is taken.
        (String::new(), false)
    } else if let Some((toast, left)) = r.toast.as_mut() {
        *left -= time.delta_secs();
        let value = toast.clone();
        if *left <= 0. {
            r.toast = None;
        }
        (value, true)
    } else {
        (String::new(), false)
    };
    if text.0 != value {
        text.0 = value;
    }
    let wanted = if show { Visibility::Visible } else { Visibility::Hidden };
    if *visibility != wanted {
        *visibility = wanted;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn session_matches_report_session_press_tap_timeout_and_reuse() {
        let mut s = ReportSession::default();
        assert!(s.press());
        assert!(!s.press(), "a held key or echo never restarts it");
        s.tick(0.2);
        assert!(s.release() && s.was_tap, "a short hold is screenshot-only");
        assert!(!s.press(), "finalizing blocks a new report");
        s.done();
        assert!(s.press());
        assert!(!s.tick(59.9));
        assert!(s.tick(0.2) && s.hit_timeout && s.state == ReportState::Finalizing);
        assert!(!s.release(), "the cap already ended it");
        s.done();
        assert!(s.press());
        s.tick(1.);
        assert!(s.release() && !s.was_tap);
    }
    #[test]
    fn config_prefers_the_launcher_key_and_reads_the_report_section() {
        let cfg = "[other]\nkey=\"x\"\n[report]\nendpoint=\"http://127.0.0.1:9/r\"\nkey=\" file \"\n";
        let c = Config::parse(cfg, None);
        assert_eq!((c.endpoint.as_str(), c.key.as_str()), ("http://127.0.0.1:9/r", "file"));
        assert_eq!(Config::parse(cfg, Some(" env ")).key, "env");
        assert_eq!(Config::parse("", Some("  ")).key, "");
    }
    #[test]
    fn console_report_command_is_recognised_exactly() {
        assert!(is_console_command("report door clips"));
        assert!(is_console_command("REPORT x"));
        assert!(is_console_command("report"));
        assert!(!is_console_command("reports x"));
        assert!(!is_console_command("teleport 1 2 3"));
        assert!(!is_console_command("abcde\u{20ac}"));
        assert!(!is_console_command("say h\u{e9}llo"));
        assert!(!is_console_command("report\u{e9}"));
    }
}
