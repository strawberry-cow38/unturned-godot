//! Headless hybrid dedicated server: the native half of source `--dedicated`
//! (game/Main.cs BuildDedicated + game/DedicatedServer.cs).
//!
//! The managed bridge owns the authoritative UDP session with no local client
//! (bevy/bridge/DedicatedHost.cs). This app runs the same world and host
//! plugins as a listen host, without a window, GPU, camera or local body, so
//! everything that needs physics keeps working: world collision, unattended
//! and NPC vehicles, zombie HOT bodies, door/deployable geometry uploads.
//! Remote drivers stay client-authoritative exactly as on a listen host.
//!
//! Boot order follows the source: build the world, register every host
//! service, then `dedicatedServe` restores the save and opens the session.
//! SIGTERM/SIGINT/SIGHUP become AppExit, which writes the final save.
use crate::{
    bridge::CoreBridge,
    cli::Options,
    entities::ServiceManifest,
    player::{GameSnapshot, PublishPlayer},
    ui::UiModel,
    world::{WorldConfig, WorldState},
};
use bevy::{
    app::{AppExit, PluginGroupBuilder, RunFixedMainLoop, RunFixedMainLoopSystems, ScheduleRunnerPlugin},
    prelude::*,
    render::{
        RenderPlugin,
        settings::{RenderCreation, WgpuSettings},
    },
    window::ExitCondition,
    winit::WinitPlugin,
};
use bevy_rapier3d::prelude::PhysicsSet;
use serde_json::{Value, json};
use std::{path::PathBuf, time::Duration};

/// Source DedicatedServer._Ready caps the headless main loop at 60 FPS
/// (`Engine.MaxFps = 60`); the simulation runs on the fixed 50 Hz schedule.
const MAIN_LOOP_HZ: f64 = 60.;
/// Frames between the map services registering and the session opening, so
/// every registration system gated on the same frame has run at least once.
const SETTLE_FRAMES: u32 = 30;
/// Loaded-region margin beyond the outermost authored object or spawn.
const EXTENT_MARGIN: f32 = 160.;
/// A listen host simulates unattended vehicles inside its streamed region
/// (180 m spawn radius + 48 m) and parks the rest. A dedicated host's region
/// is the union of those circles around every connected player.
pub const RESIDENCY_RADIUS: f32 = 228.;
/// A dedicated boot that has not opened its session by now has failed.
const BOOT_LIMIT: Duration = Duration::from_secs(15 * 60);

pub(crate) fn has_local_player(options: Res<crate::LaunchOptions>) -> bool {
    !options.0.network.dedicated()
}

/// DefaultPlugins without a window, event loop or GPU device. The render
/// world is never created (no backends), but the asset types the game's
/// world/vehicle/zombie builders allocate on the CPU stay registered.
pub fn plugins(asset_path: String) -> PluginGroupBuilder {
    DefaultPlugins
        .set(AssetPlugin {
            file_path: asset_path,
            ..default()
        })
        .set(WindowPlugin {
            primary_window: None,
            primary_cursor_options: None,
            exit_condition: ExitCondition::DontExit,
            close_when_requested: false,
            ..default()
        })
        .set(RenderPlugin {
            render_creation: RenderCreation::from(WgpuSettings {
                backends: None,
                ..default()
            }),
            ..default()
        })
        .set(ImagePlugin::default_nearest())
        .disable::<WinitPlugin>()
        .disable::<bevy::gilrs::GilrsPlugin>()
}

pub fn runner() -> ScheduleRunnerPlugin {
    ScheduleRunnerPlugin::run_loop(Duration::from_secs_f64(1. / MAIN_LOOP_HZ))
}

/// Authority rate before frame rate: a slow headless frame (a large static
/// upload, many moving vehicle hulls) runs up to one second of catch-up ticks
/// instead of Bevy's default 250 ms clamp silently slowing the world clock.
pub fn virtual_clock() -> Time<Virtual> {
    let mut clock = Time::<Virtual>::default();
    clock.set_max_delta(Duration::from_secs(1));
    clock
}

/// SIGINT/SIGTERM/SIGHUP -> Bevy's graceful AppExit. Installed explicitly
/// (overwriting) because Bevy's plugin refuses when the parent left SIGINT
/// ignored, as non-interactive shells do for background jobs.
pub fn install_signal_handler() {
    if let Err(error) = ctrlc::set_handler(bevy::app::TerminalCtrlCHandlerPlugin::gracefully_exit) {
        warn!("Cannot install the shutdown signal handler: {error}");
    }
}

/// The dedicated world is not streamed around a camera: no local body exists
/// to follow, and players can be anywhere. Load one region covering every
/// authored object and spawn (UG_DEDICATED_RADIUS overrides the radius).
pub fn configure_world(config: &mut WorldConfig) {
    let Some((center, radius)) = authored_extent(config) else {
        warn!("Dedicated world extent unavailable; using the default region");
        return;
    };
    let radius = std::env::var("UG_DEDICATED_RADIUS")
        .ok()
        .and_then(|r| r.parse::<f32>().ok())
        .filter(|r| r.is_finite() && *r > 0.)
        .unwrap_or(radius);
    config.spawn_center = Some(center);
    config.spawn_radius = radius;
    info!(
        "Dedicated world region: centre ({:.0}, {:.0}), radius {:.0} m",
        center.x, center.z, radius
    );
}

fn authored_extent(config: &WorldConfig) -> Option<(Vec3, f32)> {
    let suffix = match config.map_name.to_ascii_lowercase().as_str() {
        "washington" => "_Washington",
        "yukon" => "_Yukon",
        _ => "",
    };
    let mut points = Vec::new();
    if let Ok(text) = std::fs::read_to_string(
        config
            .content_root
            .join(format!("objects/placements{suffix}.txt")),
    ) {
        for line in text.lines() {
            let fields: Vec<_> = line.split('#').next().unwrap_or("").split_whitespace().collect();
            if let (Some(x), Some(z)) = (fields.get(1), fields.get(3)) {
                if let (Ok(x), Ok(z)) = (x.parse::<f32>(), z.parse::<f32>()) {
                    // world::placement_transform's Unity -> port negate-Z.
                    points.push(Vec2::new(x, -z));
                }
            }
        }
    }
    if let Some(root) = &config.map_root {
        if let Ok(bytes) = std::fs::read(root.join("Spawns/Players.dat")) {
            if let Ok(spawns) = crate::world::decode_player_spawns(&bytes) {
                points.extend(spawns.iter().map(|(p, _)| p.xz()));
            }
        }
        if let Ok(bytes) = std::fs::read(root.join("Spawns/Vehicles.dat")) {
            if let Ok(spawns) = crate::vehicle::spawns::decode(&bytes) {
                points.extend(spawns.iter().map(|s| s.position.xz()));
            }
        }
    }
    extent_of(&points)
}

fn extent_of(points: &[Vec2]) -> Option<(Vec3, f32)> {
    let finite = points.iter().filter(|p| p.is_finite());
    let (min, max) = finite.fold(None, |acc: Option<(Vec2, Vec2)>, p| {
        Some(acc.map_or((*p, *p), |(a, b)| (a.min(*p), b.max(*p))))
    })?;
    let center = (min + max) * 0.5;
    Some((
        Vec3::new(center.x, 0., center.y),
        (max - min).length() * 0.5 + EXTENT_MARGIN,
    ))
}

/// The dedicated additions to the host init (the world save path comes from
/// the shared WorldSaveDriver path): BanStore's list (UG_BANS_FILE wins) and
/// the retail map root for Players.dat spawns.
pub(crate) fn configure_init(options: &Options, request: &mut Value) {
    if !options.network.dedicated() {
        return;
    }
    request["bansPath"] = json!(bans_path());
    if let Some(root) = &options.unturned {
        request["mapRoot"] = json!(root.join("Maps").join(&options.map));
    }
    request["netlog"] = json!(options.netlog);
}

/// BanStore.ResolvePath with the hybrid user directory standing in for
/// Godot's `user://`.
fn bans_path() -> PathBuf {
    if let Some(path) = std::env::var_os("UG_BANS_FILE").filter(|p| !p.is_empty()) {
        return path.into();
    }
    if let Some(dir) = std::env::var_os("UG_BEVY_SAVES")
        .or_else(|| std::env::var_os("UG_SAVE_DIR"))
        .filter(|p| !p.is_empty())
    {
        return PathBuf::from(dir).join("bans.tsv");
    }
    crate::settings::Settings::path()
        .parent()
        .unwrap_or(std::path::Path::new("."))
        .join("bans.tsv")
}

pub struct DedicatedPlugin;
impl Plugin for DedicatedPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Host>()
            .init_resource::<Residency>()
            .add_systems(
                Update,
                serve
                    .after(crate::entities::WorldServicesReady)
                    .after(crate::player::PlayerUpdate),
            )
            .add_systems(
                FixedUpdate,
                tick.in_set(PublishPlayer)
                    .after(PhysicsSet::Writeback)
                    .after(crate::vehicle::VehiclePublish),
            )
            .add_systems(
                RunFixedMainLoop,
                (
                    fixed_loop_started.in_set(RunFixedMainLoopSystems::BeforeFixedMainLoop),
                    fixed_loop_finished.in_set(RunFixedMainLoopSystems::AfterFixedMainLoop),
                ),
            )
            .add_systems(Last, (heartbeat, stop));
    }
}

/// Ground positions of the connected players, from the host snapshot.
#[derive(Resource, Default)]
pub struct Residency {
    players: Vec<Vec2>,
}
impl Residency {
    pub(crate) fn outside(&self, position: Vec3) -> bool {
        self.players
            .iter()
            .all(|player| player.distance(position.xz()) > RESIDENCY_RADIUS)
    }
    fn adopt(&mut self, snapshot: &Value) {
        self.players = snapshot["players"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(|row| crate::player::vector(&row["position"]))
            .map(|p| p.xz())
            .collect();
    }
}

#[derive(Resource, Default)]
struct Host {
    settle: u32,
    /// Time since the process started waiting for the session to open.
    booting: Duration,
    connected_once: bool,
    serving: bool,
    stopped: bool,
    failed: bool,
    /// Heartbeat window: authority ticks, time spent in them, frames.
    ticks: u32,
    tick_time: Duration,
    frames: u32,
    window: Duration,
    /// Time inside the fixed (50 Hz) loop this window, Rapier included.
    fixed_time: Duration,
    fixed_started: Option<std::time::Instant>,
}

fn fixed_loop_started(mut host: ResMut<Host>) {
    host.fixed_started = Some(std::time::Instant::now());
}

fn fixed_loop_finished(mut host: ResMut<Host>) {
    if let Some(started) = host.fixed_started.take() {
        host.fixed_time += started.elapsed();
    }
}

/// Native counterpart of the bridge's 10 s `[DEDICATED] tick` heartbeat: the
/// achieved authority rate exposes a host that cannot keep its 50 Hz.
fn heartbeat(time: Res<Time<Real>>, mut host: ResMut<Host>) {
    if !host.serving || host.stopped {
        return;
    }
    host.frames += 1;
    host.window += time.delta();
    if host.window < Duration::from_secs(10) {
        return;
    }
    let seconds = host.window.as_secs_f64();
    info!(
        "[DEDICATED] native: {:.1} authority ticks/s (target 50), {:.2} ms per bridge tick, {:.2} ms per fixed step, {:.1} frames/s, {:.0} ms per frame outside the fixed loop",
        host.ticks as f64 / seconds,
        host.tick_time.as_secs_f64() * 1000. / host.ticks.max(1) as f64,
        host.fixed_time.as_secs_f64() * 1000. / host.ticks.max(1) as f64,
        host.frames as f64 / seconds,
        (host.window - host.fixed_time.min(host.window)).as_secs_f64() * 1000. / host.frames.max(1) as f64
    );
    host.fixed_time = Duration::ZERO;
    host.ticks = 0;
    host.tick_time = Duration::ZERO;
    host.frames = 0;
    host.window = Duration::ZERO;
}

/// Opens the session once the map's host services are registered. The
/// bridge restores the save first (WorldSaveDriver.LoadIntoWorld order).
fn serve(
    bridge: Res<CoreBridge>,
    config: Res<WorldConfig>,
    world: Res<WorldState>,
    services: Res<ServiceManifest>,
    session: Res<crate::player::Session>,
    time: Res<Time<Real>>,
    mut host: ResMut<Host>,
    mut model: ResMut<UiModel>,
    mut snapshot: ResMut<GameSnapshot>,
    mut exit: MessageWriter<AppExit>,
) {
    if host.serving || host.failed {
        return;
    }
    // A server that cannot start must exit (systemd restarts it) instead of
    // idling unreachable: refused init (occupied port), a lost session, or
    // map services that never register.
    if session.failed() || (host.connected_once && !model.connected) {
        let status = model.status.clone();
        return fail(&mut host, &mut exit, anyhow::anyhow!("authority session failed: {status}"));
    }
    host.booting += time.delta();
    if host.booting > BOOT_LIMIT {
        return fail(&mut host, &mut exit, anyhow::anyhow!("map services did not register within {BOOT_LIMIT:?}"));
    }
    if !world.ready || !model.connected {
        return;
    }
    host.connected_once = true;
    if services.map != config.map_name {
        host.settle = 0;
        return;
    }
    host.settle += 1;
    if host.settle < SETTLE_FRAMES {
        return;
    }
    let started = std::time::Instant::now();
    match bridge.request("dedicatedServe", json!({})) {
        Ok(mut reply) => {
            host.serving = true;
            info!(
                "Dedicated server serving on UDP {} after {:.1} s restore ({})",
                reply["result"]["port"],
                started.elapsed().as_secs_f32(),
                reply["result"]["save"]
            );
            model.adopt_snapshot(&reply["state"]);
            snapshot.0 = reply["state"].take();
        }
        Err(error) => fail(&mut host, &mut exit, error),
    }
}

/// The host's 50 Hz authority tick, at the point a listen host publishes its
/// own body (after Rapier writeback and the vehicle/zombie publications).
/// Before serving it only refreshes the snapshot so native host systems can
/// build and register the fresh world.
fn tick(
    bridge: Res<CoreBridge>,
    mut host: ResMut<Host>,
    mut residency: ResMut<Residency>,
    mut model: ResMut<UiModel>,
    mut snapshot: ResMut<GameSnapshot>,
    mut exit: MessageWriter<AppExit>,
) {
    if host.failed || host.stopped {
        return;
    }
    if !model.connected {
        if host.serving {
            fail(&mut host, &mut exit, anyhow::anyhow!("authority session lost: {}", model.status));
        }
        return;
    }
    let operation = if host.serving { "tick" } else { "snapshot" };
    let started = std::time::Instant::now();
    let result = bridge.request(operation, json!({}));
    if host.serving {
        host.ticks += 1;
        host.tick_time += started.elapsed();
    }
    match result {
        Ok(mut reply) => {
            model.adopt_snapshot(&reply["state"]);
            residency.adopt(&reply["state"]);
            snapshot.0 = reply["state"].take();
        }
        Err(error) => fail(&mut host, &mut exit, error),
    }
}

fn fail(host: &mut Host, exit: &mut MessageWriter<AppExit>, error: anyhow::Error) {
    host.failed = true;
    error!("Dedicated server authority failed: {error}");
    exit.write(AppExit::error());
}

/// Graceful shutdown: write the final save, release the socket, then end
/// the bridge child before this process drops (and kills) it.
fn stop(mut exits: MessageReader<AppExit>, bridge: Res<CoreBridge>, mut host: ResMut<Host>) {
    if exits.read().next().is_none() || host.stopped {
        return;
    }
    host.stopped = true;
    match bridge.request("dedicatedStop", json!({})) {
        Ok(reply) => info!("Dedicated server stopped: {}", reply["result"]),
        Err(error) => error!("Dedicated server stop failed: {error}"),
    }
    let _ = bridge.request("shutdown", json!({}));
}

#[cfg(test)]
mod tests;
