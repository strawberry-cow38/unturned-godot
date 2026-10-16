//! First-person viewmodels of the vehicle hand tools (EquipHeldSpraypaint /
//! EquipHeldTire / EquipHeldCarjack) from the real bridge catalog.
use super::*;
use crate::{bridge::CoreBridge, entities::CoreEvent, use_items::HandEvent};

const CAN: u16 = 1842; // Turquoise 25C891
const SPARE: u16 = 1451;
const JACK: u16 = 277;

fn catalog() -> ItemCatalog {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request("init", serde_json::json!({"contentRoot":WorldConfig::default().content_root,"name":"tool-viewmodels"}))
        .unwrap();
    let items: Vec<crate::entities::ItemVisual> =
        serde_json::from_value(bridge.request("catalog", serde_json::json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = items.into_iter().filter(|i| [CAN, SPARE, JACK].contains(&i.id)).map(|i| (i.id, i)).collect();
    catalog
}
fn install(app: &mut App, catalog: ItemCatalog, held: u16) {
    app.init_asset::<SkinnedMeshInverseBindposes>()
        .init_resource::<WorldConfig>()
        .init_resource::<crate::world::WorldGeometry>()
        .init_resource::<crate::impact::surfaces::SurfaceTable>()
        .init_resource::<crate::player::WorldSessionEpoch>()
        .init_resource::<ContentCache>()
        .insert_resource(catalog)
        .init_resource::<Settings>()
        .init_resource::<crate::use_items::HandUse>()
        .init_resource::<crate::vehicle::Occupancy>()
        .init_resource::<crate::recoil::Recoil>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<CoreEvent>()
        .add_message::<HandEvent>()
        .insert_resource(UiState { screen: Screen::Playing, ..default() })
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .insert_resource(GameSnapshot(serde_json::json!({"connected":true,"playerId":1,
            "player":{"alive":true,"heldItemId":held,"stance":"stand"}})))
        .add_plugins((crate::rig::RigPlugin, crate::particles::ParticlePlugin, ViewmodelPlugin));
}

#[test]
fn vehicle_tools_hold_their_own_clips_meshes_and_paint_tint() {
    let catalog = catalog();
    for (id, equip, use_clip, mesh, texture) in [
        (CAN, "Paint_Equip", "Paint_Use", Some("spraypaint.txt"), None),
        // EmptyHands wins the source equip precedence: bare hands, Melee_Equip.
        (SPARE, "Melee_Equip", "Tire_Use", None, None),
        (JACK, "Jack_Equip", "Jack_Use", Some("carjack.txt"), Some("carjack_albedo.png")),
    ] {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
            .init_asset::<Mesh>()
            .init_asset::<Image>()
            .init_asset::<StandardMaterial>()
            .init_asset::<AudioSource>();
        install(&mut app, ItemCatalog { items: catalog.items.clone(), ..default() }, id);
        app.world_mut().spawn((PlayerCamera, Camera::default(), Projection::default(), Transform::IDENTITY));
        for _ in 0..60 {
            app.update();
        }
        let state = app.world().resource::<ViewmodelState>();
        assert_eq!((state.hold_clip.as_str(), state.use_clip.as_str()), (equip, use_clip), "item {id}");
        let item = &catalog.items[&id];
        assert_eq!(item.hand_tool["mesh"].as_str(), mesh, "item {id}");
        assert_eq!(item.hand_tool["texture"].as_str(), texture, "item {id}");
        match (state.gun, mesh) {
            (Some(gun), Some(_)) => {
                let handle = &app.world().get::<MeshMaterial3d<StandardMaterial>>(gun).unwrap().0;
                let material = app.world().resource::<Assets<StandardMaterial>>().get(handle).unwrap();
                if id == CAN {
                    // EquipHeldSpraypaint: ConsumableColor = the can's own paint.
                    assert_eq!(material.base_color, Color::srgb(0x25 as f32 / 255., 0xc8 as f32 / 255., 0x91 as f32 / 255.));
                } else {
                    assert!(material.base_color_texture.is_some(), "the jack's 2x2 palette albedo");
                }
            }
            (None, None) => {}
            other => panic!("item {id}: held mesh {other:?}"),
        }
        let root = state.arms.unwrap();
        assert_eq!(
            app.world().get::<RigPlayback>(root).unwrap().looping,
            id == JACK,
            "item {id}: only Jack_Equip is a looping carry hold (loop: true)"
        );
        app.world_mut().write_message(HandEvent::Start(id));
        app.update();
        assert_eq!(app.world().get::<RigPlayback>(root).unwrap().clip, use_clip, "Start plays the tool's Use");
    }
}

#[cfg(test)]
mod visual {
    use super::*;
    use bevy::{
        app::AppExit,
        render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
        winit::WinitPlugin,
    };
    #[derive(Resource)]
    struct Shots {
        stage: usize,
        frame: usize,
        pending: bool,
        prefix: String,
    }
    /// (item, name, frames to hold at 20 ms, whether the Use clip is running).
    /// The jack stages match the source `--vm=DIR --gun=carjack` harness
    /// captures at 30 fps frames 25 and 60 (0.83 s, then 2.0 s on the same hold).
    const STAGES: [(u16, &str, usize, bool); 5] = [
        (CAN, "spraypaint", 60, false),
        (SPARE, "spare", 60, false),
        (JACK, "carjack", 42, false),
        (JACK, "carjack-2s", 58, false),
        (JACK, "carjack-use", 45, true),
    ];
    #[test]
    #[ignore = "Vulkan/display; UG_TOOL_VIEWMODEL_SHOT sets capture prefix"]
    fn render_first_person_spraypaint_spare_and_carjack() {
        let catalog = catalog();
        let config = WorldConfig::default();
        let mut app = App::new();
        app.add_plugins(
            DefaultPlugins
                .set(WindowPlugin {
                    primary_window: Some(Window {
                        title: "Original tool viewmodels".into(),
                        resolution: (1280, 720).into(),
                        ..default()
                    }),
                    ..default()
                })
                .set(AssetPlugin {
                    file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(),
                    ..default()
                })
                .set(WinitPlugin { run_on_any_thread: true, ..default() }),
        );
        install(&mut app, catalog, STAGES[0].0);
        app.insert_resource(ClearColor(Color::srgb(0.24, 0.27, 0.30)))
            .insert_resource(GlobalAmbientLight { brightness: 300., ..default() })
            .add_systems(Last, capture);
        app.world_mut().spawn((
            Camera3d::default(),
            PlayerCamera,
            Transform::from_xyz(0., 1.6, 0.),
        ));
        app.world_mut().spawn((
            DirectionalLight { illuminance: 9000., ..default() },
            Transform::from_xyz(-3., 8., 4.).looking_at(Vec3::ZERO, Vec3::Y),
        ));
        app.insert_resource(Shots {
            stage: 0,
            frame: 0,
            pending: false,
            prefix: std::env::var("UG_TOOL_VIEWMODEL_SHOT").unwrap_or_else(|_| "/tmp/bevy-tool-viewmodel".into()),
        });
        app.run();
    }
    fn capture(
        mut commands: Commands,
        mut shots: ResMut<Shots>,
        mut snapshot: ResMut<GameSnapshot>,
        mut hand: MessageWriter<HandEvent>,
        state: Res<ViewmodelState>,
    ) {
        if shots.pending || shots.stage >= STAGES.len() {
            return;
        }
        let (item, name, hold, using) = STAGES[shots.stage];
        snapshot.0["player"]["heldItemId"] = serde_json::json!(item);
        shots.frame += 1;
        // The swing starts once the hold is up; capture mid-crank.
        if using && shots.frame == 20 {
            hand.write(HandEvent::Start(item));
        }
        if shots.frame < hold || state.held != Some(item) {
            return;
        }
        shots.pending = true;
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{}-{name}.png", shots.prefix)))
            .observe(|_: On<ScreenshotCaptured>, mut shots: ResMut<Shots>, mut exit: MessageWriter<AppExit>| {
                shots.stage += 1;
                shots.frame = 0;
                shots.pending = false;
                if shots.stage == STAGES.len() {
                    exit.write(AppExit::Success);
                }
            });
    }
}
