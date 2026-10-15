//! Opt-in original-mesh fixture. The felling event comes from a real accepted
//! bridge shot; production foliage, resource animation and audio code render it.
use super::*;
use crate::{
    bridge::CoreBridge, content::ContentPlugin, player::PlayerCamera, settings::Settings,
    ui::Screen,
};
use bevy::{
    app::AppExit,
    core_pipeline::{prepass::DepthPrepass, tonemapping::Tonemapping},
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use serde_json::json;
#[derive(Resource)]
struct Authority(CoreBridge);
#[derive(Resource)]
struct Capture {
    frame: u32,
    sequence: u64,
    contacts: Vec<serde_json::Value>,
    saved: usize,
    directory: std::path::PathBuf,
}
#[test]
#[ignore = "requires graphics display; renders accepted resource fall to UG_RESOURCE_SHOTS"]
fn render_accepted_original_tree_fall() {
    let config = WorldConfig::default();
    let directory = std::env::var_os("UG_RESOURCE_SHOTS")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| "/tmp/tree-fall".into());
    std::fs::create_dir_all(&directory).unwrap();
    let remote = std::env::var("UG_RESOURCE_UDP").as_deref() == Ok("1");
    let (bridge, authority) = if remote {
        let (host, client) = super::tests::udp_pair(&config, Vec3::ZERO);
        (client, Some(host))
    } else {
        let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        bridge
            .request(
                "init",
                json!({"contentRoot":config.content_root,"starterKit":true,"spawn":[0,0,0]}),
            )
            .unwrap();
        (bridge, None)
    };
    let host = authority.as_ref().unwrap_or(&bridge);
    host.request("worldServices",json!({"resourceCount":1,"resources":[{"index":0,"kind":"tree","name":"Pine_0","position":[0,-0.4,-8],"health":1,"resetSeconds":40,"trunkLength":18}]})).unwrap();
    host.request(
        "worldGeometry",
        json!({"meshes":[{"resourceIndex":0,"vertices":[[-2,0,-8],[2,0,-8],[0,4,-8]]}]}),
    )
    .unwrap();
    let state = if remote {
        super::tests::pump_pair(host, &bridge, 8)
    } else {
        bridge.request("snapshot", json!({})).unwrap()["state"].clone()
    };
    let mut model = UiModel::default();
    model.connected = true;
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: config
                    .content_root
                    .canonicalize()
                    .unwrap()
                    .to_string_lossy()
                    .into(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 720).into(),
                    title: "Accepted original tree fall".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(state))
    .init_resource::<WorldGeometry>()
    .init_resource::<ServiceManifest>()
    .init_resource::<WorldOrigin>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Settings>()
    .init_resource::<RainState>()
    .insert_resource(Capture {
        frame: 0,
        sequence: 0,
        contacts: vec![],
        saved: 0,
        directory: directory.clone(),
    })
    .insert_resource(ClearColor(Color::srgb(0.36, 0.48, 0.62)))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_message::<CoreEvent>()
    .add_plugins((ContentPlugin, FoliagePlugin, ResourcePresentationPlugin))
    .add_systems(Startup, scene)
    .add_systems(Update, accepted_shot.before(present))
    .add_systems(PostUpdate, capture);
    if let Some(authority) = authority {
        app.insert_resource(Authority(authority));
    }
    app.run();
    for name in ["standing", "falling", "landed", "cleanup"] {
        assert!(directory.join(format!("{name}.png")).is_file());
    }
    if remote {
        let evidence: serde_json::Value =
            serde_json::from_slice(&std::fs::read(directory.join("cleanup.json")).unwrap())
                .unwrap();
        assert_eq!(evidence["network"]["ownsWorld"], false);
        assert_eq!(evidence["network"]["mode"], "client");
        assert_eq!(
            evidence["fixtureResourceContacts"]
                .as_array()
                .unwrap()
                .len(),
            2
        );
        assert_eq!(evidence["resources"]["dead"], json!([0]));
        assert!(
            !evidence["worldItems"].as_array().unwrap().is_empty(),
            "source authority reward timer ran"
        );
    }
}
fn scene(world: &mut World) {
    world.spawn((
        Camera3d::default(),
        PlayerCamera,
        DepthPrepass,
        Msaa::Off,
        Tonemapping::AcesFitted,
        Transform::from_xyz(28., 12., 14.).looking_at(Vec3::new(0., 7., -15.), Vec3::Y),
    ));
    world.spawn((
        DirectionalLight {
            illuminance: 6000.,
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_xyz(12., 16., 8.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    world.insert_resource(GlobalAmbientLight {
        color: Color::WHITE,
        brightness: 110.,
        ..default()
    });
    let ground = world
        .resource_mut::<Assets<Mesh>>()
        .add(Plane3d::default().mesh().size(80., 80.));
    let material = world
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial {
            base_color: Color::srgb(0.27, 0.37, 0.19),
            perceptual_roughness: 1.,
            ..default()
        });
    world.spawn((
        Mesh3d(ground),
        MeshMaterial3d(material),
        Transform::default(),
    ));
    let config = world.resource::<WorldConfig>().clone();
    let assets = world.resource::<AssetServer>().clone();
    for part in 0..2 {
        let path = config
            .content_root
            .join(format!("resources/Pine_0_{part}.obj"));
        let (mesh, _) = world
            .resource_scope(|world, mut cache: Mut<ContentCache>| {
                cache.load_mesh(&path, &mut world.resource_mut::<Assets<Mesh>>())
            })
            .unwrap();
        let material = world.resource_scope(|world, mut cache: Mut<ContentCache>| {
            cache.material(
                Some(&format!("resources/Pine_0_{part}_tex.png")),
                true,
                &assets,
                &mut world.resource_mut::<Assets<StandardMaterial>>(),
            )
        });
        let mut entity = world.spawn((
            Mesh3d(mesh),
            MeshMaterial3d(material),
            WorldResource {
                name: "Pine_0".into(),
                instance_index: 0,
            },
            Transform::from_xyz(0., -0.4, -8.).with_scale(Vec3::splat(0.8)),
            Visibility::default(),
        ));
        if part == 0 {
            entity.insert(FoliageSurface::canopy());
        }
    }
    world
        .resource_mut::<ServiceManifest>()
        .resources
        .insert(("Pine_0".into(), 0), json!({"index":0}));
    world.resource_mut::<RainState>().wind = Vec4::new(0.8, 0.6, 0.45, 1.5);
}
fn accepted_shot(
    bridge: Res<CoreBridge>,
    authority: Option<Res<Authority>>,
    mut snapshot: ResMut<GameSnapshot>,
    mut capture: ResMut<Capture>,
    mut events: MessageWriter<CoreEvent>,
    mut standing: Query<&mut Visibility, With<WorldResource>>,
) {
    capture.frame += 1;
    if capture.frame == 60 {
        authority
            .as_ref()
            .map_or(bridge.as_ref(), |host| &host.0)
            .request("action", json!({"name":"fire","args":{}}))
            .unwrap();
    }
    if let Some(host) = authority {
        host.0.request("tick", json!({})).unwrap();
    }
    let reply = bridge.request("tick", json!({})).unwrap();
    snapshot.0 = reply["state"].clone();
    for event in snapshot.0["events"].as_array().unwrap() {
        let sequence = event["seq"].as_u64().unwrap();
        if sequence <= capture.sequence {
            continue;
        }
        capture.sequence = sequence;
        if matches!(
            event["kind"].as_str(),
            Some("resourceHit" | "resourceFelled")
        ) {
            capture.contacts.push(event.clone());
        }
        events.write(CoreEvent {
            sequence,
            kind: event["kind"].as_str().unwrap().into(),
            data: event["data"].clone(),
        });
    }
    bridge.ack_events(capture.sequence);
    let dead = snapshot.0["resources"]["dead"]
        .as_array()
        .is_some_and(|a| !a.is_empty());
    for mut visibility in &mut standing {
        *visibility = if dead {
            Visibility::Hidden
        } else {
            Visibility::Inherited
        };
    }
}
fn capture(
    mut commands: Commands,
    capture: Res<Capture>,
    snapshot: Res<GameSnapshot>,
    mut exit: MessageWriter<AppExit>,
) {
    if let Some(name) = match capture.frame {
        50 => Some("standing"),
        290 => Some("falling"),
        425 => Some("landed"),
        650 => Some("cleanup"),
        _ => None,
    } {
        let path = capture.directory.join(format!("{name}.png"));
        let mut evidence = snapshot.0.clone();
        evidence["fixtureResourceContacts"] = json!(capture.contacts);
        std::fs::write(
            path.with_extension("json"),
            serde_json::to_vec_pretty(&evidence).unwrap(),
        )
        .unwrap();
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(path))
            .observe(|_: On<ScreenshotCaptured>, mut capture: ResMut<Capture>| capture.saved += 1);
    }
    if capture.saved >= 4 {
        exit.write(AppExit::Success);
    }
}
