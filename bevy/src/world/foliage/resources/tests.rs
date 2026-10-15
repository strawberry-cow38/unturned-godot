use super::*;
use crate::{settings::Settings, ui::Screen};
use serde_json::json;
use std::time::Duration;

fn app() -> (App, Entity, Entity) {
    let config = WorldConfig::default();
    let mut model = UiModel::default();
    model.connected = true;
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin {
            file_path: config.content_root.to_string_lossy().into(),
            ..default()
        },
    ))
    .insert_resource(config)
    .init_resource::<ContentCache>()
    .init_asset::<Image>()
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .init_asset::<FoliageMaterial>()
    .init_asset::<AudioSource>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<WorldGeometry>()
    .init_resource::<ServiceManifest>()
    .init_resource::<Settings>()
    .init_resource::<Materials>()
    .insert_resource(model)
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(json!({"resources":{"dead":[]}})))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        Duration::from_millis(20),
    ))
    .add_message::<CoreEvent>()
    .add_plugins(ResourcePresentationPlugin)
    .add_systems(Update, convert)
    .add_systems(PostUpdate, reaction::apply);
    let material = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial::default());
    let mut entities = vec![];
    for (instance, index, x) in [(2, 48, 0.), (3, 49, 8.)] {
        app.world_mut()
            .resource_mut::<ServiceManifest>()
            .resources
            .insert(("Pine_0".into(), instance), json!({"index":index}));
        entities.push(
            app.world_mut()
                .spawn((
                    WorldResource {
                        name: "Pine_0".into(),
                        instance_index: instance,
                    },
                    Mesh3d(Handle::default()),
                    MeshMaterial3d(material.clone()),
                    FoliageSurface::canopy(),
                    Transform::from_xyz(x, 10., 0.).with_scale(Vec3::new(0.8, 0.7, 0.8)),
                    Visibility::default(),
                ))
                .id(),
        );
    }
    app.update();
    (app, entities[0], entities[1])
}
fn event(app: &mut App, sequence: u64, kind: &str, data: serde_json::Value) {
    app.world_mut().write_message(CoreEvent {
        sequence,
        kind: kind.into(),
        data,
    });
}
#[test]
fn accepted_hit_is_immediate_per_tree_rearmed_and_releases_private_material() {
    let (mut app, tree, neighbor) = app();
    let original = app
        .world()
        .get::<MeshMaterial3d<FoliageMaterial>>(neighbor)
        .unwrap()
        .0
        .clone();
    event(
        &mut app,
        1,
        "resourceHit",
        json!({"index":48,"kind":"tree","damage":5}),
    );
    app.update();
    assert_eq!(app.world().get::<Reaction>(tree).unwrap().shake, 1.);
    assert!(app.world().get::<Reaction>(neighbor).is_none());
    let private = app
        .world()
        .get::<MeshMaterial3d<FoliageMaterial>>(tree)
        .unwrap()
        .0
        .clone();
    assert_ne!(private, original);
    assert_eq!(
        app.world()
            .resource::<Assets<FoliageMaterial>>()
            .get(&private)
            .unwrap()
            .extension
            .motion
            .settings
            .z,
        1.
    );
    assert_eq!(
        app.world()
            .resource::<Assets<FoliageMaterial>>()
            .get(&original)
            .unwrap()
            .extension
            .motion
            .settings
            .z,
        0.
    );
    app.update();
    assert!(
        (app.world().get::<Reaction>(tree).unwrap().shake - (-5.5_f32 * 0.02).exp()).abs() < 1e-6
    );
    event(
        &mut app,
        2,
        "resourceHit",
        json!({"index":48,"kind":"tree"}),
    );
    app.update();
    assert_eq!(app.world().get::<Reaction>(tree).unwrap().shake, 1.);
    for _ in 0..50 {
        app.update();
    }
    assert!(app.world().get::<Reaction>(tree).is_none());
    assert_eq!(
        app.world()
            .get::<MeshMaterial3d<FoliageMaterial>>(tree)
            .unwrap()
            .0,
        original
    );
}
#[test]
fn accepted_felling_uses_real_parts_and_retires_debris_stump_and_collision_on_regrowth() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({"index":48,"kind":"tree","direction":[1,0,0],"debrisSeconds":11}),
    );
    app.update();
    let (stump, body, debris) = {
        let r = &app.world().resource::<Registry>().remains[&48];
        (r.stump, r.body, r.debris.unwrap())
    };
    assert!(
        app.world().get::<Children>(stump).unwrap().len() > 0,
        "actual shipped stump mesh loaded"
    );
    assert_eq!(
        app.world().get::<Children>(debris).unwrap().len(),
        2,
        "actual canopy and trunk"
    );
    assert!(
        app.world().get::<Collider>(debris).is_none(),
        "original falling tree is cosmetic"
    );
    let cuboid = app
        .world()
        .get::<Collider>(body)
        .unwrap()
        .as_cuboid()
        .unwrap();
    assert!((cuboid.half_extents() - Vec3::new(0.64, 1.085, 0.64)).length() < 0.001);
    assert_eq!(
        app.world().get::<Transform>(body).unwrap().scale,
        Vec3::ONE,
        "no nonuniform Rapier parent scaling"
    );
    assert!(app.world().get::<ChildOf>(body).is_none());
    assert_eq!(
        app.world()
            .get::<CollisionGroups>(body)
            .unwrap()
            .memberships,
        Group::GROUP_1
    );
    for _ in 0..330 {
        app.update();
    }
    let f = app.world().get::<Fall>(debris).unwrap();
    assert!(
        f.sound_played,
        "measured original Timber clip started before landing"
    );
    let axis = app.world().get::<Transform>(debris).unwrap().rotation * Vec3::Y;
    assert!(
        axis.distance(Vec3::X) < 0.001,
        "source final log lies horizontal"
    );
    for _ in 0..240 {
        app.update();
    }
    assert!(app.world().get_entity(debris).is_err());
    assert!(app.world().get_entity(stump).is_ok());
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([]);
    app.update();
    assert!(app.world().get_entity(stump).is_err());
    assert!(app.world().get_entity(body).is_err());
}
#[test]
fn dead_snapshot_restores_only_stump_and_origin_epoch_cleanup_preserves_identity() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    app.update();
    let r = &app.world().resource::<Registry>().remains[&48];
    assert!(r.debris.is_none(), "no invented remote fall direction");
    let old = r.stump;
    shift_origin(app.world_mut(), Vec3::new(1024., 0., -256.));
    let tree = &app.world().resource::<Registry>().trees[&48];
    assert_eq!(tree.transform.translation, Vec3::new(-1024., 10., 256.));
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(old).is_err());
    assert_ne!(app.world().resource::<Registry>().remains[&48].stump, old);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert!(app.world().resource::<Registry>().remains.is_empty());
}
#[test]
fn source_torque_curve_settle_springs_and_sunk_base_lift() {
    let (duration, velocity) = super::super::fall::predict();
    assert!((duration - 5.9166667).abs() < 0.001);
    assert!((velocity - 1.1551914).abs() < 0.001);
    let base = Transform::from_xyz(11., -1., 7.).with_scale(Vec3::splat(2.));
    let mut fall = Fall::new(base, Vec3::NEG_Z, 22., 0.8, 11.);
    let mut largest_trunk = 0_f32;
    let mut last = base;
    let mut final_leaf = 0.;
    for _ in 0..420 {
        let (pose, leaf, trunk) = fall.step(1. / 60.);
        last = pose;
        largest_trunk = largest_trunk.max(trunk.gust.length());
        final_leaf = leaf.gust.length();
        assert!(leaf.gust.length() <= 0.030001);
    }
    assert_eq!(
        last.translation.xz(),
        base.translation.xz(),
        "source base-center pivot never drifts"
    );
    assert!(
        (last.translation.y - 1.6).abs() < 1e-5,
        "sink plus own trunk radius lift"
    );
    assert!((last.rotation * Vec3::Y).distance(Vec3::NEG_Z) < 1e-5);
    assert!(
        largest_trunk > 0.005,
        "landing kicks the independent trunk spring"
    );
    assert!(final_leaf > 0.0001, "canopy rings after rigid settle ends");
}

#[test]
fn actual_authority_shot_creates_fall_without_native_rewards_or_timer_publication() {
    let (mut app, _, _) = app();
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let bridge = crate::bridge::CoreBridge::launch(root)
        .expect("build the managed bridge with bevy/check.sh first");
    bridge
        .request(
            "init",
            json!({"contentRoot":root.join("../game/content"),"starterKit":true,"spawn":[0,10,1]}),
        )
        .unwrap();
    bridge.request("worldServices",json!({"resourceCount":50,"resources":[{"index":48,"kind":"tree","name":"Pine_0","position":[0,10,0],"health":1,"resetSeconds":30,"trunkLength":15}]})).unwrap();
    bridge
        .request(
            "worldGeometry",
            json!({"meshes":[{"resourceIndex":48,"vertices":[[-2,10,0],[2,10,0],[0,14,0]]}]}),
        )
        .unwrap();
    bridge
        .request("action", json!({"name":"fire","args":{}}))
        .unwrap();
    let mut state = serde_json::Value::Null;
    for _ in 0..6 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    assert_eq!(state["resources"]["dead"], json!([48]));
    for row in state["events"].as_array().unwrap() {
        event(
            &mut app,
            row["seq"].as_u64().unwrap(),
            row["kind"].as_str().unwrap(),
            row["data"].clone(),
        );
    }
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    let debris = app.world().resource::<Registry>().remains[&48]
        .debris
        .expect("real accepted resourceFelled reached renderer");
    for _ in 0..570 {
        app.update();
    }
    assert!(app.world().get_entity(debris).is_err());
    let state = bridge.request("snapshot", json!({})).unwrap()["state"].clone();
    assert_eq!(
        state["worldItems"].as_array().unwrap().len(),
        0,
        "native time cannot award authoritative logs"
    );
    for _ in 0..560 {
        bridge.request("tick", json!({})).unwrap();
    }
    let state = bridge.request("snapshot", json!({})).unwrap()["state"].clone();
    assert!(
        !state["worldItems"].as_array().unwrap().is_empty(),
        "original service alone emits delayed rewards"
    );
}

#[test]
fn early_regrowth_keeps_existing_fall_timer_but_session_replacement_retires_it() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({"index":48,"kind":"tree","direction":[0,0,-1],"debrisSeconds":11}),
    );
    app.update();
    let remains = &app.world().resource::<Registry>().remains[&48];
    let (stump, debris) = (remains.stump, remains.debris.unwrap());
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([]);
    app.update();
    assert!(app.world().get_entity(stump).is_err());
    assert!(app.world().get::<Fall>(debris).is_some());
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(debris).is_err());
}

#[test]
fn accepted_manifest_reindex_does_not_move_one_instances_stump_to_another_tree() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    app.update();
    let old = app.world().resource::<Registry>().remains[&48].stump;
    {
        let mut manifest = app.world_mut().resource_mut::<ServiceManifest>();
        manifest.resources.get_mut(&("Pine_0".into(), 2)).unwrap()["index"] = json!(49);
        manifest.resources.get_mut(&("Pine_0".into(), 3)).unwrap()["index"] = json!(48);
        manifest.revision += 1;
    }
    app.update();
    assert!(app.world().get_entity(old).is_err());
    let stump = app.world().resource::<Registry>().remains[&48].stump;
    assert_eq!(
        app.world().get::<Transform>(stump).unwrap().translation,
        Vec3::new(8., 10., 0.)
    );
}

#[test]
fn accepted_fall_survives_event_before_snapshot_and_retains_only_its_source_direction() {
    let (mut app, _, _) = app();
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({"index":48,"kind":"tree","direction":[1,0,0],"debrisSeconds":11}),
    );
    app.update();
    assert!(app.world().resource::<Registry>().remains.is_empty());
    assert!(app.world().resource::<Registry>().pending.contains_key(&48));
    app.update();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    app.update();
    let entity = app.world().resource::<Registry>().remains[&48]
        .debris
        .unwrap();
    let fall = app.world().get::<Fall>(entity).unwrap();
    assert_eq!(fall.direction, Vec3::X);
    assert!(
        (fall.age - 0.06).abs() < 1e-5,
        "queued delay belongs to existing fall lifetime"
    );
    assert!(app.world().resource::<Registry>().pending.is_empty());
}

#[test]
fn measured_timber_starts_before_landing_pauses_and_retires_without_audio_device() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({"index":48,"kind":"tree","direction":[0,0,-1],"debrisSeconds":11}),
    );
    app.update();
    let (entity, start) = {
        let registry = app.world().resource::<Registry>();
        (
            registry.remains[&48].debris.unwrap(),
            (super::super::fall::predict().0 - registry.timber.as_ref().unwrap().1).max(0.),
        )
    };
    while app.world().get::<Fall>(entity).unwrap().age + 0.02001 < start {
        assert!(!app.world().get::<Fall>(entity).unwrap().sound_played);
        app.update();
    }
    app.update();
    assert!(app.world().get::<Fall>(entity).unwrap().sound_played);
    let voice = app
        .world_mut()
        .query_filtered::<Entity, With<super::super::timber::Voice>>()
        .single(app.world())
        .unwrap();
    let age = app
        .world()
        .get::<super::super::timber::Voice>(voice)
        .unwrap()
        .elapsed;
    let fall_age = app.world().get::<Fall>(entity).unwrap().age;
    app.world_mut().resource_mut::<UiState>().screen = Screen::Paused;
    for _ in 0..10 {
        app.update();
    }
    assert_eq!(
        app.world()
            .get::<super::super::timber::Voice>(voice)
            .unwrap()
            .elapsed,
        age
    );
    assert_eq!(app.world().get::<Fall>(entity).unwrap().age, fall_age);
    assert!(app.world().get::<PlaybackSettings>(voice).unwrap().paused);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    let duration = app
        .world()
        .get::<super::super::timber::Voice>(voice)
        .unwrap()
        .duration;
    for _ in 0..(duration / 0.02).ceil() as usize + 2 {
        app.update();
    }
    assert!(
        app.world().get_entity(voice).is_err(),
        "finite cleanup also works without a device/sink"
    );
}

#[test]
fn remote_hit_uses_accepted_age_without_name_or_neighbor_shake() {
    let (mut app, tree, neighbor) = app();
    event(
        &mut app,
        1,
        "resourceHit",
        json!({
            "index":48,"kind":"tree","name":null,"ageSeconds":0.3,"acceptedTick":50
        }),
    );
    app.update();
    let shake = app.world().get::<Hit>(tree).unwrap().0;
    assert!((shake - (-5.5_f32 * 0.3).exp()).abs() < 1e-6);
    assert!(app.world().get::<Hit>(neighbor).is_none());
    event(
        &mut app,
        2,
        "resourceHit",
        json!({
            "index":48,"kind":"tree","name":null,"ageSeconds":2.0
        }),
    );
    app.update();
    assert!(
        app.world().get::<Hit>(tree).unwrap().0 < shake,
        "an expired reliable hit never rearms a fresh shake"
    );
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["resources"]["dead"],
        json!([])
    );
}

#[test]
fn remote_fall_adopts_age_then_waits_for_dead_snapshot_without_resetting_time() {
    let (mut app, _, _) = app();
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({
            "index":48,"kind":"tree","name":null,"direction":[1,0,0],
            "debrisSeconds":11,"ageSeconds":3.4,"acceptedTick":50
        }),
    );
    app.update();
    assert!((app.world().resource::<Registry>().pending[&48].age - 3.4).abs() < 1e-5);
    app.update();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    app.update();
    let entity = app.world().resource::<Registry>().remains[&48]
        .debris
        .unwrap();
    let fall = app.world().get::<Fall>(entity).unwrap();
    assert!(
        (fall.age - 3.46).abs() < 0.0001,
        "accepted age plus local queue time"
    );
    assert_eq!(fall.direction, Vec3::X);
    assert!(
        app.world()
            .get::<Transform>(entity)
            .unwrap()
            .rotation
            .angle_between(fall.base.rotation)
            > 2_f32.to_radians(),
        "late arrival advances the source rigid fall, not just its cleanup timer"
    );
    for _ in 0..380 {
        app.update();
    }
    assert!(
        app.world().get_entity(entity).is_err(),
        "original eleven-second deadline survives transit"
    );
    assert!(
        app.world().resource::<Registry>().remains.contains_key(&48),
        "dead bitmap still owns stump"
    );
}

#[test]
fn expired_remote_fall_keeps_dead_stump_without_replaying_debris_or_timber() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({
            "index":48,"kind":"tree","direction":[0,0,-1],"debrisSeconds":11,"ageSeconds":11
        }),
    );
    app.update();
    assert!(
        app.world().resource::<Registry>().remains[&48]
            .debris
            .is_none()
    );
    assert_eq!(
        app.world_mut().query::<&Fall>().iter(app.world()).count(),
        0
    );
    assert_eq!(
        app.world_mut()
            .query::<&super::super::timber::Voice>()
            .iter(app.world())
            .count(),
        0
    );
}

#[test]
fn delayed_remote_fall_seeks_timber_to_its_existing_timeline() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    let age = super::super::fall::predict().0 - 0.25;
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({
            "index":48,"kind":"tree","direction":[0,0,-1],"debrisSeconds":11,"ageSeconds":age
        }),
    );
    app.update();
    let voice = app
        .world_mut()
        .query_filtered::<Entity, With<super::super::timber::Voice>>()
        .single(app.world())
        .unwrap();
    let settings = app.world().get::<PlaybackSettings>(voice).unwrap();
    let elapsed = app
        .world()
        .get::<super::super::timber::Voice>(voice)
        .unwrap()
        .elapsed;
    let seek = settings.start_position.unwrap().as_secs_f32();
    assert!(
        seek > 0.5,
        "late event must not replay timber from its start"
    );
    assert!((seek / settings.speed - (elapsed - 0.02)).abs() < 0.0001);
    let duration = app
        .world()
        .get::<super::super::timber::Voice>(voice)
        .unwrap()
        .duration;
    let remaining = duration - elapsed;
    assert!(
        remaining < duration * 0.5,
        "late delivery leaves only the existing clip remainder"
    );
    for _ in 0..(remaining / 0.02).ceil() as usize + 2 {
        app.update();
    }
    assert!(
        app.world().get_entity(voice).is_err(),
        "voice ends on its existing remainder"
    );
}

pub(super) fn pump_pair(
    host: &crate::bridge::CoreBridge,
    client: &crate::bridge::CoreBridge,
    count: usize,
) -> serde_json::Value {
    let mut state = serde_json::Value::Null;
    for _ in 0..count {
        host.request("tick", json!({})).unwrap();
        state = client.request("tick", json!({})).unwrap()["state"].clone();
        std::thread::sleep(Duration::from_millis(1));
    }
    state
}
pub(super) fn udp_pair(
    config: &WorldConfig,
    spawn: Vec3,
) -> (crate::bridge::CoreBridge, crate::bridge::CoreBridge) {
    let socket = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
    let port = socket.local_addr().unwrap().port();
    drop(socket);
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let host = crate::bridge::CoreBridge::launch(root).expect("Build managed bridge first");
    let client = crate::bridge::CoreBridge::launch(root).expect("Build managed bridge first");
    host.request(
        "init",
        json!({"contentRoot":config.content_root,"starterKit":true,
        "spawn":spawn.to_array(),"listenPort":port,"allowCheats":true}),
    )
    .unwrap();
    client
        .request(
            "init",
            json!({"contentRoot":config.content_root,"connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
    pump_pair(&host, &client, 20);
    client.request("action",json!({"name":"console","args":{"text":format!("teleport {} {} {}",spawn.x+10.,spawn.y,spawn.z)}})).unwrap();
    pump_pair(&host, &client, 12);
    (host, client)
}

#[test]
fn actual_udp_resource_contact_materializes_remote_fall_without_native_authority() {
    let (mut app, _, _) = app();
    let config = app.world().resource::<WorldConfig>().clone();
    let (host, client) = udp_pair(&config, Vec3::new(0., 10., 1.));
    host.request(
        "worldServices",
        json!({"resourceCount":50,"resources":[{
        "index":48,"kind":"tree","name":"Pine_0","position":[0,10,0],
        "health":1,"resetSeconds":30,"trunkLength":15}]}),
    )
    .unwrap();
    host.request(
        "worldGeometry",
        json!({"meshes":[{"resourceIndex":48,
        "vertices":[[-2,10,0],[2,10,0],[0,14,0]]}]}),
    )
    .unwrap();
    pump_pair(&host, &client, 8);
    host.request("action", json!({"name":"fire","args":{}}))
        .unwrap();
    let state = pump_pair(&host, &client, 16);
    assert_eq!(state["network"]["ownsWorld"], false);
    assert_eq!(state["resources"]["dead"], json!([48]));
    let facts: Vec<_> = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|e| matches!(e["kind"].as_str(), Some("resourceHit" | "resourceFelled")))
        .collect();
    assert_eq!(
        facts.len(),
        2,
        "actual reliable remote hit and fall must arrive"
    );
    for row in facts {
        assert!(
            row["data"]["name"].is_null(),
            "identity comes from accepted manifest index"
        );
        event(
            &mut app,
            row["seq"].as_u64().unwrap(),
            row["kind"].as_str().unwrap(),
            row["data"].clone(),
        );
    }
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    let remains = &app.world().resource::<Registry>().remains[&48];
    let debris = remains
        .debris
        .expect("wire event reaches the real native resource presenter");
    assert!(app.world().get::<Fall>(debris).unwrap().direction.z < -0.99);
    for _ in 0..570 {
        app.update();
    }
    assert!(app.world().get_entity(debris).is_err());
    let before = host.request("snapshot", json!({})).unwrap()["state"].clone();
    assert_eq!(before["player"]["ammo"], 29);
    assert!(
        before["worldItems"].as_array().unwrap().is_empty(),
        "render time awards no logs"
    );
    let after = pump_pair(&host, &client, 550);
    assert!(
        !after["worldItems"].as_array().unwrap().is_empty(),
        "original host timer awards logs once"
    );
    assert_eq!(after["resources"]["dead"], json!([48]));
}

#[test]
fn delayed_fall_after_timber_ends_keeps_remaining_debris_silently() {
    let (mut app, _, _) = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["resources"]["dead"] = json!([48]);
    event(
        &mut app,
        1,
        "resourceFelled",
        json!({
            "index":48,"kind":"tree","direction":[0,0,-1],"debrisSeconds":11,"ageSeconds":10
        }),
    );
    app.update();
    let entity = app.world().resource::<Registry>().remains[&48]
        .debris
        .unwrap();
    assert!(app.world().get::<Fall>(entity).unwrap().age > 10.);
    assert_eq!(
        app.world_mut()
            .query::<&super::super::timber::Voice>()
            .iter(app.world())
            .count(),
        0,
        "the shipped8.256second clip already ended, including its source pitch range"
    );
    for _ in 0..51 {
        app.update();
    }
    assert!(app.world().get_entity(entity).is_err());
}
