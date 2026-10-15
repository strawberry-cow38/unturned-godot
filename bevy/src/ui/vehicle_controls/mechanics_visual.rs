//! Production MechanicsPanel layout at720p, including source disabled captions.
use super::*;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, save_to_disk},
    winit::WinitPlugin,
};

#[test]
#[ignore = "requires Vulkan/display; UG_MECHANICS_SHOT sets PNG path"]
fn actual_panel_rows_do_not_wrap_and_disabled_captions_remain_dim() {
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1280, 720).into(),
                    title: "Original mechanics rows".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    );
    app.init_resource::<InteractionFocus>()
        .insert_resource(ItemCatalog::default())
        .insert_resource(UiState { screen:Screen::Mechanics,..default() })
        .insert_resource(Menu { mechanics:Some(7),..default() })
        .insert_resource(GameSnapshot(json!({"connected":true,"player":{"alive":true},"vehicles":[{
            "netId":7,"typeId":data::named("police").unwrap().type_id,"flags":0,
            "health":6000,"fuel":60000,"battery":10000,"lampMask":127,"brokenLampMask":1,"glassMask":63,"brokenGlassMask":1,"tireMask":15,"poppedTireMask":2}]})))
        .add_systems(Startup, |mut commands:Commands| { commands.spawn(Camera2d); })
        .add_systems(Update,(build,refresh).chain())
        .add_systems(PostUpdate, capture.after(bevy::ui::UiSystems::Layout));
    let entity = crate::vehicle::tests::body(&mut app, "police", Vec3::ZERO);
    app.world_mut()
        .get_mut::<VehicleBody>(entity)
        .unwrap()
        .net_id = 7;
    app.world_mut().resource_mut::<ItemCatalog>().vehicle_lighting=Some(serde_json::from_value(json!({
        "lampLabels":["headlight_l","headlight_r","taillight_l","taillight_r","siren0","siren1","lightbar_c"],
        "patternNames":[],"sirenPitch":[],"low":{"range":45,"energy":9,"angle":25,"attenuation":1.3},
        "high":{"range":98,"energy":17,"angle":21,"attenuation":0.85}
    })).unwrap());
    let bridge =
        crate::bridge::CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))
            .unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":crate::world::WorldConfig::default().content_root,"seed":0}),
        )
        .unwrap();
    let metadata = bridge.request("catalog", json!({})).unwrap();
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .vehicle_maintenance =
        Some(serde_json::from_value(metadata["result"]["vehicleMaintenance"].clone()).unwrap());
    app.run();
}
fn capture(
    mut frame: Local<u32>,
    mut commands: Commands,
    mut exit: MessageWriter<AppExit>,
    rows: Query<(&PartText, &ComputedNode, &bevy::text::TextLayoutInfo)>,
    captions: Query<(&FixText, &TextColor)>,
    mut scroll: Query<&mut ScrollPosition, With<Scroll>>,
) {
    *frame += 1;
    if matches!(*frame, 20 | 40) {
        let path = std::env::var("UG_MECHANICS_SHOT")
            .unwrap_or_else(|_| "/tmp/bevy-mechanics-panel.png".into());
        let path = std::path::Path::new(&path);
        let path = path.with_file_name(format!(
            "{}-{}.png",
            path.file_stem().unwrap().to_string_lossy(),
            if *frame == 20 { "top" } else { "bottom" }
        ));
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(path));
    }
    if *frame == 30 {
        for mut position in &mut scroll {
            position.y = 2000.;
        }
    }
    if *frame == 50 {
        assert_eq!(rows.iter().count(), 17);
        for (label, node, layout) in &rows {
            assert!(
                layout.size.y < 24.,
                "source NoWrap row{} became{}px tall",
                label.1,
                layout.size.y
            );
            assert!(node.size().x >= 210.);
            assert_eq!(
                layout.run_geometry.len(),
                1,
                "one original line per source part"
            );
        }
        for (caption, color) in &captions {
            let broken = match caption.0 {
                Part::Glass | Part::Lamp => caption.1 == 0,
                Part::Tire => caption.1 == 1,
            };
            assert_eq!(color.0.alpha(), if broken { 1. } else { 0.5 });
        }
        exit.write(AppExit::Success);
    }
}
