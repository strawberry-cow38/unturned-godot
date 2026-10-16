use super::*;
use crate::player::PlayerBody;
use std::path::Path;

#[test]
fn crop_registry_reads_seed_names_and_dirt_with_the_source_fallback() {
    let config = WorldConfig::default();
    let real = registry(&std::fs::read_to_string(config.content_root.join("crops.tsv")).unwrap());
    assert_eq!(real.len(), 20);
    assert_eq!(real[&330], CropDef { name: "carrot".into(), dirt: [0.559, 0.388, 0.131] });
    let odd = registry("bad\t7\t0.1,0.2\tModel_0\nshort\t8\nzero\t0\t1,1,1\n");
    assert_eq!(odd[&7].dirt, DEFAULT_DIRT, "a malformed _Color falls back to (0.4,0.32,0.2)");
    assert_eq!(odd.len(), 1, "short rows and seed 0 never resolve");
}

#[test]
fn harvest_scan_takes_the_nearest_grown_crop_strictly_inside_three_metres() {
    let crops = [(1, true, Vec3::new(2.5, 0., 0.)), (2, false, Vec3::new(0.5, 0., 0.)), (3, true, Vec3::new(0., 0., 1.5)), (4, true, Vec3::Z * 3.)];
    assert_eq!(nearest_grown(Vec3::ZERO, crops.into_iter()), Some(3), "young crops never match");
    assert_eq!(nearest_grown(Vec3::new(0., 0., 6.), crops.into_iter()), None, "3 m is outside (d < reach²)");
}

#[test]
fn fall_through_sits_below_look_targets_but_above_storage() {
    let mut focus = InteractionFocus::default();
    focus.fall_through("harvestCrop", json!({"netId":5}));
    assert!(focus.selected("harvestCrop", 5));
    let c = focus.candidate.as_ref().unwrap();
    assert!(c.label.is_empty() && !c.show_binding, "the source harvest has no prompt");
    let mut focus = InteractionFocus::default();
    focus.offer(1., "Open Shelf", "openStorage", json!({"netId":9}), true);
    focus.fall_through("harvestCrop", json!({"netId":5}));
    assert!(focus.selected("harvestCrop", 5), "harvest is tried before _focusShelf/OpenNearestCrate");
    assert_eq!(focus.candidate.as_ref().unwrap().label, "Open Shelf", "the shelf prompt still shows");
    let mut focus = InteractionFocus::default();
    focus.offer(1., "Sit", "sitSeat", json!({"netId":9}), false);
    focus.fall_through("harvestCrop", json!({"netId":5}));
    assert!(focus.selected("sitSeat", 9), "every other look target wins");
}

fn rig() -> App {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let config = WorldConfig::default();
    bridge.request("init", json!({"contentRoot":config.content_root,"name":"crops","spawn":[0,0,0]})).unwrap();
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin { file_path: config.content_root.to_string_lossy().into(), ..default() }))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .insert_resource(bridge)
        .insert_resource(config)
        .insert_resource(GameSnapshot(Value::Null))
        .init_resource::<UiModel>()
        .insert_resource(UiState { screen: crate::ui::Screen::Playing, ..default() })
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<InteractionFocus>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<Crops>()
        .add_systems(Update, (reset_focus, sync, offer_harvest, apply_interaction).chain());
    app.world_mut().spawn((PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"), Transform::default()));
    app
}
fn tick(app: &mut App) -> Value {
    let mut state = Value::Null;
    for _ in 0..8 {
        state = app.world().resource::<CoreBridge>().request("tick", json!({})).unwrap()["state"].clone();
    }
    app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
    state
}
fn console(app: &App, text: &str) {
    app.world().resource::<CoreBridge>().request("action", json!({"name":"console","args":{"text":text}})).unwrap();
}
/// One F edge for one frame (MinimalPlugins has no input clearing).
fn press_f(app: &mut App) {
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::KeyF);
    app.update();
    let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
    keys.release(KeyCode::KeyF);
    keys.clear();
}
fn replicas(app: &mut App) -> Vec<(u32, bool, Vec3, Vec<Visibility>)> {
    let mut q = app.world_mut().query::<(&CropReplica, &Transform, &Children)>();
    let rows: Vec<_> = q.iter(app.world()).map(|(c, t, k)| (c.net_id, c.grown, t.translation, k.to_vec())).collect();
    rows.into_iter()
        .map(|(id, grown, at, kids)| (id, grown, at, kids.iter().map(|k| *app.world().get::<Visibility>(*k).unwrap()).collect()))
        .collect()
}

#[test]
fn console_plant_replicates_a_crop_and_f_harvests_only_a_grown_one_through_the_authority() {
    let mut app = rig();
    tick(&mut app);
    console(&app, "plant carrot");
    let state = tick(&mut app);
    let texts: Vec<_> = state["events"].as_array().unwrap().iter().filter(|e| e["kind"] == "console").map(|e| e["data"]["text"].clone()).collect();
    assert!(texts.iter().any(|t| t == "planted carrot -- UG_FARMSPEED speeds growth; E near a grown crop to harvest"), "{texts:?}");
    app.update();
    let young = replicas(&mut app);
    assert_eq!(young.len(), 1);
    let (young_id, grown, at, parts) = young[0].clone();
    assert!(!grown);
    assert_eq!(parts, [Visibility::Inherited, Visibility::Inherited, Visibility::Hidden], "dirt + Foliage_0 shown, Foliage_1 hidden");
    assert!((at - Vec3::new(0., 1.75, -2.6)).length() < 0.3, "PlayerController.LookPoint: the 2.6 m eye ray end: {at}");
    // Stand beside it: a young crop never takes F.
    app.world_mut().query_filtered::<&mut Transform, With<PlayerBody>>().single_mut(app.world_mut()).unwrap().translation = at - Vec3::Y * 1.5 + Vec3::Z;
    press_f(&mut app);
    assert!(!app.world().resource::<InteractionFocus>().has_candidate());
    console(&app, "plant corn grown");
    tick(&mut app);
    app.update();
    let both = replicas(&mut app);
    let (grown_id, _, _, parts) = both.iter().find(|r| r.0 != young_id).unwrap().clone();
    assert_eq!(parts, [Visibility::Inherited, Visibility::Hidden, Visibility::Inherited]);
    assert!(app.world().resource::<InteractionFocus>().selected("harvestCrop", grown_id));
    assert_eq!(app.world().resource::<UiModel>().interaction, "", "no prompt of its own");
    // ALT-look: Interact interacts with nothing.
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::AltLeft);
    press_f(&mut app);
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().release(KeyCode::AltLeft);
    tick(&mut app);
    app.update();
    assert!(replicas(&mut app).iter().any(|r| r.0 == grown_id), "Alt+F harvests nothing");
    press_f(&mut app);
    assert!(replicas(&mut app).iter().any(|r| r.0 == grown_id), "F is an intent; the crop leaves only when the server retires it");
    let state = tick(&mut app);
    app.update();
    let left: Vec<_> = replicas(&mut app).iter().map(|r| r.0).collect();
    assert_eq!(left, [young_id], "the accepted harvest retires the grown crop only");
    assert!(state["worldItems"].as_array().unwrap().iter().any(|i| i["itemId"] == 335), "corn yield (Grow 335) drops at the crop");
    console(&app, "plant");
    console(&app, "plant cabbage");
    let texts: Vec<_> = tick(&mut app)["events"].as_array().unwrap().iter().filter(|e| e["kind"] == "console").map(|e| e["data"]["text"].as_str().unwrap().to_owned()).collect();
    assert!(texts.contains(&"plant: needs an argument (or add it to NoArgVerbs if it should run bare)".to_owned()), "{texts:?}");
    assert!(texts.contains(&"no crop 'cabbage' (try: carrot, corn, wheat, potato, tomato, pumpkin...)".to_owned()), "{texts:?}");
}

#[test]
fn look_point_is_the_first_hit_within_the_source_reach_else_the_ray_end() {
    let camera = Transform::from_xyz(0., 1.7, 0.).looking_to(Vec3::new(0., -1., -1.), Vec3::Y);
    let forward = *camera.forward();
    assert!(look_point(&camera, |_, _, reach| { assert_eq!(reach, 2.6); Some(1.) }).distance(camera.translation + forward) < 1e-5);
    assert!(look_point(&camera, |_, _, _| None).distance(camera.translation + forward * 2.6) < 1e-5);
    assert!(is_console_command("PLANT carrot") && !is_console_command("planted") && !is_console_command(""));
}

/// The production registration: harvest runs after every CollectInteractions
/// offer, so a storage look target yields F and a seat keeps it.
#[test]
fn installed_harvest_runs_after_the_look_targets_it_ranks_against() {
    for (storage, wins) in [(true, "harvestCrop"), (false, "sitSeat")] {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(UiState { screen: crate::ui::Screen::Playing, ..default() })
            .insert_resource(GameSnapshot(json!({"player":{"alive":true,"vehicleNetId":0}})))
            .init_resource::<InteractionFocus>()
            .add_systems(Update, reset_focus.before(CollectInteractions))
            .add_systems(Update, (move |mut focus: ResMut<InteractionFocus>| {
                let action = if storage { "openStorage" } else { "sitSeat" };
                focus.offer(1., "target", action, json!({"netId":7}), storage);
            }).in_set(CollectInteractions));
        install_harvest(&mut app);
        app.world_mut().spawn((PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"), Transform::default()));
        app.world_mut().spawn((CropReplica { net_id: 3, grown: true, young: None, mature: None }, Transform::from_xyz(1., 0., 0.)));
        app.update();
        let id = if wins == "harvestCrop" { 3 } else { 7 };
        assert!(app.world().resource::<InteractionFocus>().selected(wins, id), "storage={storage}");
    }
}

mod visual;
