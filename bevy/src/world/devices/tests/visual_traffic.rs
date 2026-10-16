//! `Main.BuildTrafficLightDemo` (`--trafficlight`): one Traffic_Light_0 stood
//! up at the origin over an 80 m ground at night, both heads pinned to an
//! aspect (`ForcePhase`), through the production attach and lens apply.
//! `UG_TL_STATE` = green|amber|red|flash|dark or "a/b" per head, `UG_TL_SIDE=1`
//! (flash red), `UG_TL_LEVEL`, `UG_TL_SHOT`.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use traffic::{Head, Phase};

#[derive(Resource)]
struct Shot(String, u32, bool);

#[test]
#[ignore = "Vulkan/display; UG_TL_SHOT=PNG, UG_TL_STATE, UG_TL_SIDE, UG_TL_LEVEL"]
fn render_source_traffic_light_demo() {
    let config = WorldConfig::default();
    let root = config.content_root.canonicalize().unwrap();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original traffic light".into(),
                    resolution: (1280, 720).into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: root.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<crate::nightvision::NightVision>()
    .add_plugins((crate::hdr::HdrPlugin, crate::content::ContentPlugin))
    .insert_resource(config)
    .insert_resource(GameSnapshot(json!({})))
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<Mains>()
    .init_resource::<traffic::SideRoads>()
    .insert_resource(ClearColor(Color::srgb(0.02, 0.03, 0.05)))
    .insert_resource(GlobalAmbientLight {
        color: Color::srgb(0.05, 0.06, 0.09),
        brightness: crate::hdr::ambient_brightness(1.),
        ..default()
    })
    .insert_resource(Shot(
        std::env::var("UG_TL_SHOT").unwrap_or_else(|_| "/tmp/bevy-trafficlight.png".into()),
        0,
        false,
    ))
    .add_systems(Startup, scene)
    .add_systems(Update, (traffic::attach, force, traffic::tick, capture).chain());
    app.run();
}

fn scene(
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(80., 80.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.20, 0.20, 0.19),
            perceptual_roughness: 1.,
            ..default()
        })),
    ));
    let (mesh, _) = cache.load_mesh(&config.content_root.join("objects/Traffic_Light_0.obj"), &mut meshes).unwrap();
    // The real 4x2 palette, nearest (linear would bleed the red lens into the amber).
    let base = cache.material(Some("objects/Traffic_Light_0_tex.png"), false, &assets, &mut materials);
    let mut body = materials.get(&base).unwrap().clone();
    body.perceptual_roughness = 0.8;
    body.double_sided = true;
    body.cull_mode = None;
    // Basis(X, 270) at the origin == placement_transform's Y(180 - 180) X(270).
    let euler = Vec3::new(270., 180., 0.);
    commands.spawn((
        Mesh3d(mesh),
        MeshMaterial3d(materials.add(body)),
        crate::world::placement_transform(Vec3::ZERO, euler, Vec3::ONE),
        WorldObject { guid: String::new(), asset_name: "Traffic_Light_0".into(), source_index: 1 },
        PlacementEuler(euler),
    ));
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Projection::Perspective(PerspectiveProjection {
            fov: 55f32.to_radians(),
            ..default()
        }),
        Transform::from_xyz(-9., 6.6, -5.4).looking_at(Vec3::new(0., 6.3, -5.4), Vec3::Y),
    ));
}

fn force(mut heads: Query<&mut Head>) {
    let side = std::env::var("UG_TL_SIDE").as_deref() == Ok("1");
    let states = std::env::var("UG_TL_STATE").unwrap_or_else(|_| "red".into()).to_lowercase();
    let states: Vec<&str> = states.split('/').collect();
    let level: f32 = std::env::var("UG_TL_LEVEL").ok().and_then(|v| v.parse().ok()).unwrap_or(1.);
    for mut head in &mut heads {
        let h = head.index;
        if head.frozen {
            continue;
        }
        head.side_road = side;
        let phase = match states[h % states.len()] {
            "green" => Phase::Green,
            "amber" => Phase::Amber,
            "dark" => Phase::Off,
            "flash" if side => Phase::FlashRed,
            "flash" => Phase::FlashAmber,
            _ => Phase::Red,
        };
        head.force(phase, level);
    }
}

fn capture(mut commands: Commands, mut shot: ResMut<Shot>, heads: Query<&Head>) {
    shot.1 += 1;
    if shot.2 || shot.1 < 60 {
        return;
    }
    for head in &heads {
        eprintln!("[trafficlight] phase={:?} lens={:?} level={}", head.phase, head.lit(), head.level);
    }
    shot.2 = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.0.clone()))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}
