//! Actual accepted bus cabin rendered through the production inventory builder.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, save_to_disk},
    winit::WinitPlugin,
};
#[test]
#[ignore = "requires Vulkan/display; UG_STORAGE_SHOT chooses output prefix"]
fn accepted_bus_cabin_keeps_all_named_pockets_and_scrolls_independently() {
    let content = WorldConfig::default().content_root;
    let core = bridge();
    core.request(
        "init",
        json!({"contentRoot":content,"starterKit":true,"spawn":[0,0,0]}),
    )
    .unwrap();
    let bus = data::named("bus").unwrap();
    let row = crate::vehicle::registration(bus, "bus-cabin".into(), Vec3::new(0., 0., -1.), 0., 0);
    let id = core
        .request("worldVehicles", json!({"vehicles":[row]}))
        .unwrap()["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap();
    action(&core, "enterVehicle", json!({"netId":id,"seat":0}));
    for _ in 0..10 {
        core.request("tick", json!({})).unwrap();
    }
    action(
        &core,
        "openVehicleStorage",
        json!({"vehicleNetId":id,"kind":1}),
    );
    for _ in 0..10 {
        core.request("tick", json!({})).unwrap();
    }
    let state = core.request("snapshot", json!({})).unwrap()["state"].clone();
    let mut model = UiModel::default();
    model.adopt_snapshot(&state);
    assert_eq!(model.storage.label, "Glovebox");
    assert_eq!(model.storage.compartments.len(), bus.seats.len().min(12));
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(AssetPlugin {
                file_path: content.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 720).into(),
                    title: "Accepted bus cabin".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    );
    app.insert_resource(model)
        .insert_resource(UiState {
            screen: Screen::Inventory,
            ..default()
        })
        .insert_resource(crate::entities::ItemCatalog::default())
        .add_systems(Startup, build)
        .add_systems(PostUpdate, capture.after(bevy::ui::UiSystems::Layout));
    app.run();
}
fn build(
    mut commands: Commands,
    state: Res<UiState>,
    model: Res<UiModel>,
    assets: Res<AssetServer>,
    catalog: Res<crate::entities::ItemCatalog>,
) {
    commands.spawn(Camera2d);
    commands
        .spawn(Node {
            width: percent(100),
            height: percent(100),
            justify_content: JustifyContent::Center,
            align_items: AlignItems::Center,
            ..default()
        })
        .with_children(|root| {
            root.spawn((
                Node {
                    width: px(860),
                    padding: UiRect::all(px(24)),
                    flex_direction: FlexDirection::Column,
                    row_gap: px(12),
                    ..default()
                },
                BackgroundColor(PANEL),
            ))
            .with_children(|p| inventory_panel(p, &state, &model, &assets, &catalog));
        });
}
fn capture(
    mut frame: Local<u32>,
    mut commands: Commands,
    mut exit: MessageWriter<AppExit>,
    mut columns: Query<(&Column, &mut ScrollPosition)>,
    texts: Query<&Text>,
    model: Res<UiModel>,
) {
    *frame += 1;
    if *frame == 30 {
        for (column, mut scroll) in &mut columns {
            if column.0 == 1 {
                scroll.y = 10000.;
            }
        }
    }
    if matches!(*frame, 20 | 40) {
        let prefix =
            std::env::var("UG_STORAGE_SHOT").unwrap_or_else(|_| "/tmp/bevy-bus-cabin".into());
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!(
                "{prefix}-{}.png",
                if *frame == 20 { "top" } else { "bottom" }
            )));
    }
    if *frame == 50 {
        assert_eq!(columns.iter().count(), 2);
        for (column, scroll) in &columns {
            if column.0 == 0 {
                assert_eq!(scroll.y, 0.);
            } else {
                assert!(scroll.y > 0.);
            }
        }
        for pocket in &model.storage.compartments {
            assert!(texts.iter().any(|t| t.0 == pocket.label));
        }
        assert!(texts.iter().any(|t| t.0 == "Nearby"));
        exit.write(AppExit::Success);
    }
}
