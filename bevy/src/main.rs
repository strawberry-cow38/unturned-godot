mod avatar;
mod bridge;
mod bugreport;
mod bullets;
mod character;
mod cli;
mod content;
mod dedicated;
mod deployables;
mod entities;
mod environment;
mod hdr;
mod nightvision;
mod outline;
mod network;
mod impact;
mod install;
mod info_billboard;
mod locomotion;
mod particles;
mod player;
mod profile;
mod projectiles;
mod recoil;
mod rig;
mod settings;
mod sound;
mod survival;
mod footsteps;
mod graphics;
mod ui;
mod use_items;
mod vehicle;
mod viewmodel;
mod weapons;
mod wildlife;
mod world;
mod zombie;

use anyhow::Result;
use bevy::{
    app::AppExit,
    image::ImagePlugin,
    prelude::*,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    window::PresentMode,
};
use bevy_rapier3d::prelude::*;
use bridge::CoreBridge;
use cli::Options;
use serde_json::json;

#[derive(Resource)]
struct LaunchOptions(Options);

fn main() -> Result<()> {
    let mut options = Options::parse(std::env::args().skip(1))?;
    if options.help {
        println!("{}", cli::HELP);
        return Ok(());
    }
    let bridge = CoreBridge::launch_installed(install::Layout::current())?;
    if options.headless {
        return headless_check(&bridge, &options);
    }
    let dedicated = options.network.dedicated();
    // A dedicated host has no local profile or display settings to load.
    let profile = if dedicated {
        profile::LocalProfile::default()
    } else {
        profile::LocalProfile::load(&bridge, &mut options)
    };
    let mut config = world::WorldConfig {
        content_root: options.content.clone(),
        map_name: options.map.clone(),
        map_root: options
            .unturned
            .as_ref()
            .map(|path| path.join("Maps").join(&options.map)),
        ..default()
    };
    if options.map.eq_ignore_ascii_case("procedural") {
        let generated =
            bridge.request("terrain", json!({"seed":options.seed,"x":0,"z":0,"lod":0}))?;
        let directory = install::Layout::current().data_dir();
        std::fs::create_dir_all(&directory)?;
        let path = directory.join(format!("terrain-{}.json", options.seed));
        std::fs::write(&path, serde_json::to_vec(&generated["result"])?)?;
        config.procedural_path = Some(path);
    }
    world::finite::bootstrap::prepare(&bridge, &mut options, &mut config)?;
    if dedicated {
        if !config.map_root.as_ref().is_some_and(|root| root.join("Level.dat").is_file()) {
            anyhow::bail!(
                "--dedicated needs the retail {} map (set UG_UNTURNED_DIR or --unturned)",
                options.map
            );
        }
        dedicated::configure_world(&mut config);
    }
    let settings = if dedicated {
        settings::Settings::default()
    } else {
        settings::Settings::load().unwrap_or_else(|error| {
            eprintln!("Could not load settings: {error}");
            settings::Settings::default()
        })
    };
    let present_mode = if settings.vsync {
        PresentMode::AutoVsync
    } else {
        PresentMode::AutoNoVsync
    };
    let asset_path = options
        .content
        .canonicalize()
        .map_err(|e| anyhow::anyhow!("locating bundled game content: {e}"))?;
    let boot = graphics::Boot::from(&settings, options.fixed_fps.is_some());
    let mut app = App::new();
    if let Some(fps) = options.fixed_fps {
        let frame = std::time::Duration::from_secs_f64(1. / f64::from(fps));
        let mut clock = Time::<Virtual>::default();
        clock.set_max_delta(clock.max_delta().max(frame));
        app.insert_resource(clock);
        app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(frame));
    }
    let mut defaults = DefaultPlugins
        .set(AssetPlugin {
            file_path: asset_path.to_string_lossy().into_owned(),
            ..default()
        })
        .set(WindowPlugin {
            primary_window: Some(Window {
                title: "Unturned · Bevy".into(),
                resolution: (1280, 720).into(),
                present_mode,
                mode: graphics::window_mode(settings.graphics.fullscreen),
                ..default()
            }),
            ..default()
        })
        .set(ImagePlugin::default_nearest());
    if !boot.render_thread {
        // GraphicsOptions render thread "Single": read once at launch.
        defaults = defaults.disable::<bevy::render::pipelined_rendering::PipelinedRenderingPlugin>();
    }
    app.insert_resource(bridge)
        .insert_resource(boot)
        .insert_resource(profile)
        .insert_resource(config)
        .insert_resource(settings)
        .insert_resource(Time::<Fixed>::from_hz(50.0))
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .insert_resource(LaunchOptions(options))
        .insert_resource(ClearColor(Color::srgb(0.48, 0.67, 0.81)));
    if dedicated {
        dedicated::install_signal_handler();
        app.insert_resource(dedicated::virtual_clock());
        app.add_plugins((
            dedicated::plugins(asset_path.to_string_lossy().into_owned()),
            dedicated::runner(),
        ));
    } else {
        app.add_plugins(defaults);
    }
    add_game_plugins(&mut app, dedicated);
    if dedicated {
        app.add_plugins(dedicated::DedicatedPlugin);
        // A signal is the normal stop (Bevy's handler exits with 130); only a
        // failed authority exits non-zero, so systemd records a crash.
        let exit = app.run();
        // Drop first: the bridge child and its runtime copy are reaped by drop.
        drop(app);
        if let AppExit::Error(code) = exit {
            if code.get() != 130 {
                std::process::exit(i32::from(code.get()));
            }
        }
    } else {
        app.add_systems(Update, capture_frame.after(entities::ApplyInteractions))
            .run();
    }
    Ok(())
}

/// The game and host plugins, shared by the windowed client and the headless
/// dedicated server (which keeps them for their host-authority systems).
fn add_game_plugins(app: &mut App, dedicated: bool) {
    app.add_plugins((
        RapierPhysicsPlugin::<vehicle::PhysicsHooks>::default().in_fixed_schedule(),
        content::ContentPlugin,
        particles::ParticlePlugin,
        projectiles::ProjectilePlugin,
        entities::EntityPlugin,
        deployables::DeployablePlugin,
        environment::EnvironmentPlugin,
        world::WorldPlugin,
        rig::RigPlugin,
        viewmodel::ViewmodelPlugin,
        ui::GameUiPlugin,
        player::PlayerPlugin,
        vehicle::VehiclePlugin,
        wildlife::WildlifePlugin,
        impact::ImpactPlugin,
    ))
    .add_plugins(info_billboard::InfoBillboardPlugin)
    .add_plugins((
        hdr::HdrPlugin,
        nightvision::NightVisionPlugin,
        character::CharacterPlugin,
        zombie::ZombiePlugin,
        bullets::BulletPlugin,
        avatar::AvatarPlugin,
        footsteps::FootstepPlugin,
        survival::SurvivalPlugin,
    ));
    // Screen-only presentation: the window/camera/frame limiter, the bug
    // reporter (which records the microphone) and the focus outline have
    // nothing to act on without a window or a local player, and the frame
    // limiter would throttle the headless authority loop.
    if !dedicated {
        app.add_plugins((
            graphics::GraphicsPlugin,
            bugreport::BugReportPlugin,
            outline::OutlinePlugin,
        ));
    }
}

fn capture_frame(
    mut commands: Commands,
    options: Res<LaunchOptions>,
    world: Res<world::WorldState>,
    snapshot: Res<player::GameSnapshot>,
    viewmodel: Res<viewmodel::ViewmodelStatus>,
    lights: Res<viewmodel::lights::Switches>,
    light_facts: Res<viewmodel::lights::LightDiagnostics>,
    lean: Res<player::lean::LeanState>,
    profile: Res<profile::LocalProfile>,
    dashboard: (
        Res<ui::MapDiagnostics>,
        Res<ui::UiState>,
        Res<ui::crafting::Menu>,
        Res<deployables::InteractionDiagnostics>,
    ),
    keys: Res<ButtonInput<KeyCode>>,
    mut frames: Local<u32>,
    particles: Query<(&particles::Emitter, &GlobalTransform)>,
    mut events: MessageReader<entities::CoreEvent>,
    mut pending_event: Local<Option<entities::CoreEvent>>,
    mounted: (
        Res<vehicle::turrets::Operator>,
        Res<ui::tank_optics::TankOptics>,
        Res<player::flinch::Flinch>,
        Query<&Transform, With<player::PlayerCamera>>,
    ),
) {
    if !world.ready {
        return;
    }
    *frames += 1;
    // Dynamic particle meshes need one extraction/upload before a useful shot.
    // Capture the following frame while the short blast is still alive.
    let event = pending_event.take();
    *pending_event = events
        .read()
        .find(|event| options.0.screenshot_event.as_deref() == Some(event.kind.as_str()))
        .cloned();
    let timed = options.0.screenshot_event.is_none() && *frames == options.0.screenshot_frame;
    if !timed && event.is_none() && !keys.just_pressed(KeyCode::F12) {
        return;
    }
    let Some(path) = &options.0.screenshot else {
        return;
    };
    if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        if let Err(error) = std::fs::create_dir_all(parent) {
            error!("Cannot create capture directory: {error}");
            return;
        }
    }
    let should_exit = options.0.exit_after_capture;
    // Keep the authority snapshot beside each render so visual review can also
    // verify position, stance, health and the entities that should be visible.
    let effects: Vec<_> = particles
        .iter()
        .map(|(emitter, pose)| {
            let (age, count) = emitter.debug_state();
            json!({"position":pose.translation().to_array(),"age":age,"particles":count})
        })
        .collect();
    let event = event.map(|e| json!({"seq":e.sequence,"kind":e.kind,"data":e.data}));
    let evidence = json!({"map":options.0.map,"fixedFps":options.0.fixed_fps,"renderFrame":*frames,"spawn":world.spawn.to_array(),"state":snapshot.0,"profilePreference":{"ready":profile.ready,"name":profile.name,"face":profile.face,"hasAvatar":profile.has_avatar},"presentation":{"held":viewmodel.held,"ready":viewmodel.ready,"aiming":viewmodel.aiming,"inspecting":viewmodel.inspecting,"aimAlpha":viewmodel.aim_alpha,"scopeZoom":viewmodel.scope_zoom,"lean":{"angleRadians":lean.angle,"eyeHeight":lean.eye_height},"lights":{"tacticalId":lights.tactical_id,"tactical":lights.tactical_lit,"held":lights.held_lit,"head":lights.head_lit,"nightvision":lights.nightvision,"military":lights.military}},"information":dashboard.0.0,"interface":{"screen":format!("{:?}",dashboard.1.screen),"capturesInput":dashboard.1.captures_input(),"crafting":dashboard.2.diagnostics()},"lastCompletedFrameLights":light_facts.0,"particles":effects,"captureEvent":event});
    let mut evidence = evidence;
    evidence["deployableInteraction"] = serde_json::to_value(&*dashboard.3).unwrap_or_default();
    evidence["world"] = json!({"description":world.description,"terrainAvailable":world.terrain_available,
        "objects":world.object_count,"foliage":world.foliage_count,"seaLevel":world.sea_level,"missingAssets":world.missing_assets,
        "finiteDirectory":options.0.island.as_ref().or(options.0.new_island.as_ref())});
    evidence["mountedPresentation"] = json!({
        "vehicle": mounted.0.seat.map(|s| s.net_id),
        "seat": mounted.0.seat.map(|s| s.seat),
        "slot": mounted.0.slot,
        "ammo": mounted.0.active().and_then(|m| m.ammo),
        "headOut": mounted.0.head_out,
        "optic": format!("{:?}", mounted.1.mode),
        "zoom": mounted.1.zoom(),
        "flinchDegrees": mounted.2.angle_degrees(),
        "camera": mounted.3.single().ok().map(|t| json!({"position":t.translation.to_array(),"rotation":t.rotation.to_array()})),
    });
    if let Err(error) = std::fs::write(path.with_extension("json"), evidence.to_string()) {
        warn!("Cannot write capture state: {error}");
    }
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(path.clone()))
        .observe(
            move |_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
                if should_exit {
                    exit.write(AppExit::Success);
                }
            },
        );
}

fn headless_check(bridge: &CoreBridge, options: &Options) -> Result<()> {
    let init = bridge.request("init", json!({"contentRoot":options.content,"name":"Bevy smoke","spawn":[0,0,0],"mapId":options.map,"starterKit":true}))?;
    if init["result"]["protocolVersion"] != 1 {
        anyhow::bail!("Unsupported game bridge version");
    }
    let mut position = [0.0_f32; 3];
    for _ in 0..options.ticks {
        let response = bridge.request(
            "simulate",
            json!({"input":{"moveX":0,"moveY":1,"grounded":true,"stance":"stand","yaw":0}}),
        )?;
        let velocity: [f32; 3] = serde_json::from_value(response["result"]["velocity"].clone())?;
        for axis in 0..3 {
            position[axis] += velocity[axis] * 0.02;
        }
        bridge.request("tick",json!({"input":{"position":position,"velocity":velocity,"grounded":true,"stance":"stand","yaw":0}}))?;
    }
    let snapshot = bridge.request("snapshot", json!({}))?;
    if snapshot["state"]["connected"] != true {
        anyhow::bail!("Local server disconnected during smoke test");
    }
    let expected = -4.5 * 0.02 * options.ticks as f32;
    if (position[2] - expected).abs() > 0.02 {
        anyhow::bail!("Unexpected movement trace: {position:?}");
    }
    println!(
        "Bevy core smoke passed: {} ticks, position {:?}, server connected, {} catalog items",
        options.ticks, position, init["result"]["catalogItems"]
    );
    Ok(())
}
