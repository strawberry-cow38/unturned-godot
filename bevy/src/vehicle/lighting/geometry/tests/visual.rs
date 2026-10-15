//! Stationary original aircraft with accepted integrated-host ignition and the
//! production source spool/nav/beacon clocks. This is not a flight-input test.
use super::*;
use crate::{
    bridge::CoreBridge,
    entities::ItemCatalog,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::Settings,
    ui::{UiModel, UiState},
    vehicle::{VehicleBody, data, render},
    world::WorldConfig,
};
use bevy::{
    app::AppExit,
    ecs::system::RunSystemOnce,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use serde_json::json;
use std::path::Path;

#[derive(Resource)]
struct Scene {
    bridge: CoreBridge,
    body: Entity,
    id: u32,
    key: &'static str,
    frame: u32,
    prefix: String,
    lit_shot: bool,
}
#[derive(Resource, Default)]
struct Captures(usize);
#[test]
#[ignore = "requires Vulkan and frozen protocol68 bridge; UG_LAMP_AIRCRAFT=huey|hind UG_LAMP_AIRCRAFT_SIDE=-1|1 UG_LAMP_AIRCRAFT_SHOT=prefix"]
fn render_accepted_aircraft_navigation_and_belly_beacon() {
    let key = if std::env::var("UG_LAMP_AIRCRAFT").as_deref() == Ok("hind") {
        "hind"
    } else {
        "huey"
    };
    let root = WorldConfig::default();
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":root.content_root,"spawn":[20,0,20]}),
        )
        .unwrap();
    let definition = data::named(key).unwrap();
    let at = Vec3::Y * 3.;
    let registration =
        crate::vehicle::registration(definition, "aircraft-lens-fixture".into(), at, 0., 0);
    let id = bridge
        .request("worldVehicles", json!({"vehicles":[registration]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    // Registry replication arrives at its real network cadence. Do not treat
    // registration's host result as an already-adopted local replica.
    let mut adopted = false;
    for _ in 0..30 {
        let state = bridge.request("tick", json!({})).unwrap();
        adopted = state["state"]["vehicles"]
            .as_array()
            .is_some_and(|rows| rows.iter().any(|r| r["netId"] == id));
        if adopted {
            break;
        }
    }
    assert!(
        adopted,
        "registered aircraft must reach the local client replica"
    );
    let metadata = bridge.request("catalog", json!({})).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.vehicle_lighting =
        Some(serde_json::from_value(metadata["result"]["vehicleLighting"].clone()).unwrap());
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: root
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1100, 760).into(),
                    title: format!("Original {key} aircraft lamps"),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<crate::nightvision::NightVision>()
    .add_plugins(crate::hdr::HdrPlugin)
    .init_resource::<ContentCache>()
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(root)
    .insert_resource(catalog)
    .insert_resource(GameSnapshot(json!({})))
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(ClearColor(Color::srgb(0.012, 0.018, 0.025)))
    .insert_resource(Captures::default())
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    let mut model = UiModel::default();
    model.connected = true;
    app.insert_resource(model);
    let body = crate::vehicle::tests::body(&mut app, key, at);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flight =
        crate::vehicle::flight::Flight::new(
            &definition.spec,
            crate::vehicle::flight::ignition_duration(
                &definition.spec,
                &WorldConfig::default().content_root,
            ),
        );
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>,
                  config: Res<WorldConfig>| {
                render::build(
                    &mut commands,
                    body,
                    data::named(key).unwrap(),
                    0,
                    &config.content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    let side = if std::env::var("UG_LAMP_AIRCRAFT_SIDE").as_deref() == Ok("-1") {
        -1.
    } else {
        1.
    };
    app.world_mut().spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Transform::from_xyz(side * 9., 1., -12.).looking_at(Vec3::new(0., 3.5, 1.), Vec3::Y),
    ));
    app.world_mut().spawn((
        DirectionalLight {
            illuminance: crate::hdr::directional_illuminance(0.15),
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_rotation(Quat::from_euler(EulerRot::XYZ, -0.8, -0.5, 0.)),
    ));
    app.insert_resource(GlobalAmbientLight {
        color: Color::srgb(0.5, 0.6, 0.8),
        brightness: crate::hdr::ambient_brightness(0.05),
        ..default()
    });
    app.insert_resource(Scene {
        bridge,
        body,
        id,
        key,
        frame: 0,
        prefix: std::env::var("UG_LAMP_AIRCRAFT_SHOT")
            .unwrap_or_else(|_| format!("/tmp/bevy-{key}-lamps")),
        lit_shot: false,
    })
    .add_systems(First, step)
    .add_systems(
        Update,
        (
            super::super::super::presentation::build,
            super::super::super::presentation::advance,
            super::super::super::presentation::update,
            render::aircraft::animate,
        )
            .chain(),
    )
    .add_systems(
        PostUpdate,
        capture.after(bevy::transform::TransformSystems::Propagate),
    );
    app.run();
}
fn step(world: &mut World) {
    let mut scene = world.remove_resource::<Scene>().unwrap();
    scene.frame += 1;
    if scene.frame == 40 {
        scene
            .bridge
            .request(
                "worldVehicleState",
                json!({"vehicles":[{"netId":scene.id,"flags":1}]}),
            )
            .unwrap();
    }
    let snapshot = scene.bridge.request("tick", json!({})).unwrap()["state"].clone();
    let row = snapshot["vehicles"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["netId"] == scene.id)
        .unwrap();
    {
        let mut body = world.get_mut::<VehicleBody>(scene.body).unwrap();
        body.flags = row["flags"].as_u64().unwrap() as u8;
        body.health = row["health"].as_f64().unwrap() as f32;
        let running = body.flags & 1 != 0;
        body.flight.advance_presentation(
            &data::named(scene.key).unwrap().spec,
            running,
            false,
            0.02,
        );
    }
    world.resource_mut::<GameSnapshot>().0 = snapshot;
    assert!(scene.frame < 400, "aircraft lamp captures timed out");
    world.insert_resource(scene);
}
fn capture(
    mut commands: Commands,
    mut scene: ResMut<Scene>,
    shots: Res<Captures>,
    snapshot: Res<GameSnapshot>,
    bodies: Query<(&VehicleBody, &BuiltLighting)>,
    materials: Res<Assets<StandardMaterial>>,
    mut exit: MessageWriter<AppExit>,
) {
    if shots.0 == 2 {
        exit.write(AppExit::Success);
        return;
    }
    let (body, built) = bodies.get(scene.body).unwrap();
    let beacon = built
        .lenses
        .iter()
        .find(|l| l.kind == Kind::Beacon)
        .unwrap();
    let lit = materials.get(&beacon.material).unwrap().emissive.red > 0.;
    let label = if scene.frame == 30 {
        Some("off")
    } else if scene.frame > 85 && lit && !scene.lit_shot {
        scene.lit_shot = true;
        Some("lit")
    } else {
        None
    };
    let Some(label) = label else {
        return;
    };
    let lenses = built
        .lenses
        .iter()
        .map(|l| {
            let m = materials.get(&l.material).unwrap();
            json!({"kind":format!("{:?}",l.kind),"center":l.center.to_array(),
        "albedo":m.base_color.to_srgba().to_f32_array(),"emission":m.emissive.to_f32_array()})
        })
        .collect::<Vec<_>>();
    if label == "lit" {
        assert!(body.flags & 1 != 0 && body.flight.spool > 0.02);
    }
    std::fs::write(format!("{}-{label}.json",scene.prefix),serde_json::to_vec_pretty(&json!({
        "fixture":"stationary original aircraft; accepted integrated-host ignition and source presentation spool",
        "key":scene.key,"frame":scene.frame,"state":snapshot.0,"spool":body.flight.spool,"lenses":lenses})).unwrap()).unwrap();
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}-{label}.png", scene.prefix)))
        .observe(|_: On<ScreenshotCaptured>, mut shots: ResMut<Captures>| shots.0 += 1);
}
