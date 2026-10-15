mod bridge;
mod cli;
mod content;
mod entities;
mod environment;
mod locomotion;
mod player;
mod projectiles;
mod rig;
mod settings;
mod ui;
mod use_items;
mod viewmodel;
mod vehicle;
mod weapons;
mod world;

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
use std::path::Path;

#[derive(Resource)]
struct LaunchOptions(Options);

fn main() -> Result<()> {
    let options = Options::parse(std::env::args().skip(1))?;
    if options.help {
        println!("{}", cli::HELP);
        return Ok(());
    }
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR")))?;
    if options.headless {
        return headless_check(&bridge, &options);
    }
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
        let directory = Path::new(env!("CARGO_MANIFEST_DIR")).join(".local");
        std::fs::create_dir_all(&directory)?;
        let path = directory.join(format!("terrain-{}.json", options.seed));
        std::fs::write(&path, serde_json::to_vec(&generated["result"])?)?;
        config.procedural_path = Some(path);
    }
    let settings = settings::Settings::load().unwrap_or_else(|error| {
        eprintln!("Could not load settings: {error}");
        settings::Settings::default()
    });
    let present_mode = if settings.vsync {
        PresentMode::AutoVsync
    } else {
        PresentMode::AutoNoVsync
    };
    let asset_path = options
        .content
        .canonicalize()
        .map_err(|e| anyhow::anyhow!("locating bundled game content: {e}"))?;
    let mut app = App::new();
    app.insert_resource(bridge)
        .insert_resource(config)
        .insert_resource(settings)
        .insert_resource(Time::<Fixed>::from_hz(50.0))
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .insert_resource(LaunchOptions(options))
        .insert_resource(ClearColor(Color::srgb(0.48, 0.67, 0.81)))
        .add_plugins(
            DefaultPlugins
                .set(AssetPlugin {
                    file_path: asset_path.to_string_lossy().into_owned(),
                    ..default()
                })
                .set(WindowPlugin {
                    primary_window: Some(Window {
                        title: "Unturned · Bevy".into(),
                        resolution: (1280, 720).into(),
                        present_mode,
                        ..default()
                    }),
                    ..default()
                })
                .set(ImagePlugin::default_nearest()),
        )
        .add_plugins((
            RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
            content::ContentPlugin,
            projectiles::ProjectilePlugin,
            entities::EntityPlugin,
            environment::EnvironmentPlugin,
            world::WorldPlugin,
            rig::RigPlugin,
            viewmodel::ViewmodelPlugin,
            ui::GameUiPlugin,
            player::PlayerPlugin,
            vehicle::VehiclePlugin,
        ))
        .add_systems(Update, capture_frame)
        .run();
    Ok(())
}

fn capture_frame(
    mut commands: Commands,
    options: Res<LaunchOptions>,
    world: Res<world::WorldState>,
    snapshot: Res<player::GameSnapshot>,
    keys: Res<ButtonInput<KeyCode>>,
    mut frames: Local<u32>,
) {
    if !world.ready {
        return;
    }
    *frames += 1;
    if *frames != options.0.screenshot_frame && !keys.just_pressed(KeyCode::F12) {
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
    let evidence = json!({"map":options.0.map,"renderFrame":*frames,"spawn":world.spawn.to_array(),"state":snapshot.0});
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
