use super::*;
use crate::{
    content::ContentCache,
    particles::{Emitter, ParticlePlugin},
    settings::Settings,
    world::WorldConfig,
};
use bevy::ecs::system::RunSystemOnce;
use serde_json::json;

fn app() -> (App, Entity) {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_asset::<AudioSource>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<Settings>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<UiModel>()
        .insert_resource(crate::ui::UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .add_message::<CoreEvent>()
        .add_plugins(ParticlePlugin)
        .add_systems(Update, receive);
    app.world_mut().resource_mut::<UiModel>().connected = true;
    let body = crate::vehicle::tests::body(&mut app, "police", Vec3::ZERO);
    app.world_mut().get_mut::<Transform>(body).unwrap().rotation = Quat::from_rotation_y(1.2);
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  config: Res<WorldConfig>| {
                let (_, built) = crate::vehicle::lighting::geometry::build(
                    &mut commands,
                    body,
                    crate::vehicle::data::named("police").unwrap(),
                    &config.content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                );
                commands.entity(body).insert(built);
            },
        )
        .unwrap();
    app.update();
    (app, body)
}
fn fact(app: &mut App, sequence: u64, kind: &str, label: &str) {
    app.world_mut().write_message(CoreEvent {
        sequence,
        kind: kind.into(),
        data: json!({"netId":1,"label":label,"position":[13.,5.,-7.],"acceptedTick":1040}),
    });
}
fn bursts(app: &mut App) -> Vec<Entity> {
    app.world_mut()
        .query_filtered::<Entity, With<Emitter>>()
        .iter(app.world())
        .collect()
}
#[test]
fn accepted_lamp_break_uses_source_albedo_box_face_and_does_not_replay_snapshot_or_repair() {
    let (mut app, body) = app();
    app.update();
    assert!(
        bursts(&mut app).is_empty(),
        "building a damaged replica alone is not a break event"
    );
    let (material, half) = {
        let built = app.world().get::<BuiltLighting>(body).unwrap();
        let lamp = built
            .lamps
            .iter()
            .find(|l| l.label == "taillight_l")
            .unwrap();
        (
            lamp.material.clone().unwrap(),
            lamp.half_extents.max(Vec3::splat(0.06)),
        )
    };
    let source = Color::srgb(0.8, 0.3, 0.2);
    app.world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .get_mut(&material)
        .unwrap()
        .base_color = source;
    fact(&mut app, 1, "vehicleLampBroken", "taillight_l");
    app.update();
    let burst = bursts(&mut app);
    assert_eq!(burst.len(), 1);
    let entity = burst[0];
    let emitter = app.world().get::<Emitter>(entity).unwrap();
    let spec = emitter.source_spec();
    assert_eq!(spec.amount, 6);
    assert_eq!(
        spec.emission_box,
        Some(half),
        "hull yaw must not rotate the source world-aligned box"
    );
    assert!((spec.direction - Quat::from_rotation_y(1.2) * Vec3::Z).length() < 0.00001);
    assert_eq!(
        spec.colors[0].1,
        Vec4::from_array(source.with_alpha(0.5).to_linear().to_f32_array())
    );
    assert_eq!(
        app.world().get::<Transform>(entity).unwrap().translation,
        Vec3::new(13., 5., -7.)
    );
    assert!(
        app.world().get::<ChildOf>(entity).is_none(),
        "shards must not follow moving hulls"
    );
    assert!(
        app.world()
            .get::<bevy::camera::visibility::NoFrustumCulling>(entity)
            .is_some()
    );
    fact(&mut app, 1, "vehicleLampBroken", "taillight_l");
    fact(&mut app, 2, "vehicleLampRepaired", "taillight_l");
    app.update();
    assert_eq!(bursts(&mut app).len(), 1);
    fact(&mut app, 3, "vehicleLampBroken", "lightbar_c");
    app.update();
    let amp = bursts(&mut app).into_iter().find(|e| *e != entity).unwrap();
    let spec = app.world().get::<Emitter>(amp).unwrap().source_spec();
    assert_eq!(spec.emission_box, Some(Vec3::new(0.18, 0.08, 0.12)));
    assert_eq!(
        spec.colors[0].1,
        Vec4::from_array(
            Color::srgba(0.94, 0.89, 0.73, 0.5)
                .to_linear()
                .to_f32_array()
        )
    );
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(bursts(&mut app).is_empty());
}

#[test]
fn delayed_break_facts_keep_first_lens_color_and_repaired_lightbar_gray_across_skipped_cycles() {
    use super::super::presentation;
    use crate::{entities::ItemCatalog, player::GameSnapshot};
    let (mut app, body) = app();
    let bridge =
        crate::bridge::CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))
            .unwrap();
    let config = WorldConfig::default();
    bridge
        .request("init", json!({"contentRoot":config.content_root}))
        .unwrap();
    let metadata = bridge.request("catalog", json!({})).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.vehicle_lighting =
        Some(serde_json::from_value(metadata["result"]["vehicleLighting"].clone()).unwrap());
    app.insert_resource(catalog)
        .insert_resource(GameSnapshot(json!({})));
    app.world_mut()
        .run_system_once(presentation::build)
        .unwrap();
    let lens = app
        .world()
        .get::<BuiltLighting>(body)
        .unwrap()
        .lamps
        .iter()
        .find(|l| l.label == "lightbar_l")
        .unwrap()
        .material
        .clone()
        .unwrap();
    let red = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(&lens)
        .unwrap()
        .base_color;
    let gray = Color::srgb(0.12, 0.12, 0.12);
    let adopt = |app: &mut App, tick, mask| {
        app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"tick":tick,"vehicles":[{
            "netId":1,"flags":0,"brokenLampMask":mask}]});
        app.world_mut()
            .run_system_once(presentation::advance)
            .unwrap();
        app.world_mut()
            .run_system_once(presentation::update)
            .unwrap();
    };
    adopt(&mut app, 10, 0);
    adopt(&mut app, 50, 16); // Local snapshot50 overtakes the server reliable fact at tick1040.
    assert_eq!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(&lens)
            .unwrap()
            .base_color,
        gray
    );
    adopt(&mut app, 60, 0); // Even a repair snapshot arrives before the first fact.
    fact(&mut app, 10, "vehicleLampBroken", "lightbar_l");
    app.update();
    let first = bursts(&mut app)[0];
    assert_eq!(
        app.world()
            .get::<Emitter>(first)
            .unwrap()
            .source_spec()
            .colors[0]
            .1,
        Vec4::from_array(red.with_alpha(0.5).to_linear().to_f32_array())
    );
    // A complete later break/repair cycle may be absent from snapshots. Reliable
    // events still retain the original permanent grey after the first break.
    fact(&mut app, 11, "vehicleLampRepaired", "lightbar_l");
    fact(&mut app, 12, "vehicleLampBroken", "lightbar_l");
    app.update();
    let second = bursts(&mut app).into_iter().find(|e| *e != first).unwrap();
    assert_eq!(
        app.world()
            .get::<Emitter>(second)
            .unwrap()
            .source_spec()
            .colors[0]
            .1,
        Vec4::from_array(gray.with_alpha(0.5).to_linear().to_f32_array())
    );
    assert_eq!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(&lens)
            .unwrap()
            .base_color,
        gray
    );
    app.world_mut().despawn(body);
    assert_eq!(
        app.world_mut()
            .query::<&BreakColors>()
            .iter(app.world())
            .count(),
        0,
        "color history belongs to the vehicle lifetime"
    );
}

#[test]
fn early_break_fact_and_damaged_late_join_do_not_reintroduce_lightbar_color() {
    let red = Color::srgb(0.8, 0.1, 0.1);
    let gray = Color::srgb(0.12, 0.12, 0.12);
    let cream = Color::srgb(0.94, 0.89, 0.73);
    let mut early = BreakColors::default();
    assert_eq!(early.take("lightbar_l", red), red);
    assert!(early.dark_bar("lightbar_l"));
    early.observe("lightbar_l", false, red, gray); // skipped damaged snapshot
    assert_eq!(early.take("lightbar_l", gray), gray);
    let mut late = BreakColors::default();
    late.observe("lightbar_l", true, red, gray); // join already damaged
    late.observe("lightbar_l", false, gray, gray); // repaired
    assert_eq!(late.take("lightbar_l", gray), gray);
    // Head/tail repairs restore their original albedo rather than sticky grey.
    late.observe("headlight_l", true, cream, gray);
    late.observe("headlight_l", false, gray, cream);
    assert_eq!(late.take("headlight_l", cream), cream);
}
