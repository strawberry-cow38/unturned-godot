use super::*;
use serde_json::{Value, json};

fn app() -> App {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_asset::<AudioSource>()
        .init_resource::<ItemCatalog>()
        .init_resource::<WorldConfig>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<Settings>()
        .insert_resource(model)
        .insert_resource(GameSnapshot(json!({"playerId":1})))
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .add_message::<CoreEvent>()
        .add_message::<crate::player::flinch::ExplosionFlinch>()
        .add_plugins((particles::ParticlePlugin, MountedEffectsPlugin));
    app.world_mut().spawn((
        PlayerCamera,
        Transform::from_xyz(-8., 5., -10.).looking_at(Vec3::ZERO, Vec3::Y),
    ));
    app.update();
    app.update();
    app
}
fn send(app: &mut App, sequence: u64, data: Value) {
    app.world_mut().write_message(CoreEvent {
        sequence,
        kind: "mountShot".into(),
        data,
    });
}
fn count<T: Component>(app: &mut App) -> usize {
    app.world_mut()
        .query_filtered::<Entity, With<T>>()
        .iter(app.world())
        .count()
}
#[test]
fn actual_authority_cannon_fact_uses_its_muzzle_smoke_audio_and_operator_flinch_once() {
    let bridge =
        crate::bridge::CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))
            .unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,"spawn":[0,0,0]}),
        )
        .unwrap();
    for _ in 0..12 {
        bridge.request("tick", json!({})).unwrap();
    }
    let data = bridge.request("catalog", json!({})).unwrap();
    let definitions: HashMap<String, f32> =
        serde_json::from_value(data["result"]["mountedWeapons"].clone()).unwrap();
    assert!(definitions["tank_cannon"] > 0.);
    assert_eq!(definitions["hmg"], 0.);
    let reply = bridge
        .request(
            "worldVehicles",
            json!({"vehicles":[{"typeId":27,"key":"fx-test",
        "position":[0,0,0],"health":10000,"fuel":100,"seatCount":2}]}),
        )
        .unwrap();
    let id = reply["result"]["vehicles"][0]["netId"].as_u64().unwrap();
    bridge
        .request(
            "action",
            json!({"name":"enterVehicle","args":{"netId":id,"seat":0}}),
        )
        .unwrap();
    for _ in 0..12 {
        bridge.request("tick", json!({})).unwrap();
    }
    bridge
        .request(
            "action",
            json!({"name":"mountInput","args":{"netId":id,"aimMode":"point",
        "target":[0,2.8,-600],"trigger":true}}),
        )
        .unwrap();
    let mut state = Value::Null;
    for _ in 0..8 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    let shot = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .find(|e| e["kind"] == "mountShot")
        .unwrap()["data"]
        .clone();
    let muzzle = crate::player::vector(&shot["muzzle"]).unwrap();
    let direction = crate::player::vector(&shot["direction"]).unwrap();
    let mut app = app();
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .mounted_weapons = definitions;
    let vehicle = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    app.world_mut()
        .get_mut::<VehicleBody>(vehicle)
        .unwrap()
        .net_id = id as u32;
    send(&mut app, 1, shot.clone());
    send(&mut app, 1, shot.clone());
    app.update();
    assert_eq!(
        count::<Flash>(&mut app),
        1,
        "duplicate accepted fact must not replay its flash"
    );
    let (parent, pose) = app
        .world_mut()
        .query::<(&ChildOf, &Transform)>()
        .iter(app.world())
        .find(|(p, _)| app.world().get::<Lifetime>(p.parent()).is_some())
        .unwrap();
    assert_eq!(pose.scale, Vec3::splat(2.2));
    let at = app
        .world()
        .get::<Transform>(parent.parent())
        .unwrap()
        .translation;
    assert!(at.distance(muzzle + direction * 0.77) < 0.001);
    let light = app
        .world_mut()
        .query::<&PointLight>()
        .single(app.world())
        .unwrap();
    assert!((light.range - 12. * 2.2_f32.sqrt()).abs() < 0.001);
    assert_eq!(light.intensity, crate::hdr::point_lumens(5.));
    assert_eq!(count::<particles::Emitter>(&mut app), 1);
    assert_eq!(
        app.world_mut()
            .query::<&particles::Emitter>()
            .single(app.world())
            .unwrap()
            .debug_state()
            .1,
        12
    );
    assert_eq!(
        count::<voices::Voice>(&mut app),
        2,
        "report plus source breech reload cue"
    );
    assert_eq!(
        app.world()
            .resource::<Messages<crate::player::flinch::ExplosionFlinch>>()
            .len(),
        1
    );
    // A second shot from another operator draws identical cannon FX but does
    // not kick this local camera; held weapon metadata is never consulted.
    let mut remote = shot;
    remote["operatorPlayerId"] = json!(2);
    send(&mut app, 2, remote);
    app.update();
    assert_eq!(
        app.world()
            .resource::<Messages<crate::player::flinch::ExplosionFlinch>>()
            .len(),
        1
    );
    assert_eq!(count::<Flash>(&mut app), 2);
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    for _ in 0..10 {
        app.update();
    }
    assert_eq!(
        count::<Flash>(&mut app),
        2,
        "SP pause freezes the50ms flash clock"
    );
    app.world_mut().resource_mut::<UiState>().networked = true;
    for _ in 0..4 {
        app.update();
    }
    assert_eq!(
        count::<Flash>(&mut app),
        0,
        "MP menu leaves world effects running"
    );
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert_eq!(
        count::<Transient>(&mut app),
        0,
        "session reset retires sound and smoke as well"
    );
}

#[test]
fn source_gatling_report_cap_keeps_each_flash_and_npc_cannon_has_original_scale() {
    let mut app = app();
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .mounted_weapons = HashMap::from([("hmg".into(), 0.), ("tank_cannon".into(), 9.)]);
    for sequence in 1..=3 {
        send(
            &mut app,
            sequence,
            json!({"gun":"hmg","muzzle":[1,2,3],
        "direction":[0,0,-1],"npc":true,"operatorPlayerId":0}),
        );
    }
    app.update();
    assert_eq!(count::<Flash>(&mut app), 3);
    let flashes: Vec<_> = app
        .world_mut()
        .query_filtered::<(
            &MeshMaterial3d<material::FlashMaterial>,
            &bevy::mesh::MeshTag,
            &Transform,
            Has<NoFrustumCulling>,
            Has<NotShadowCaster>,
        ), With<Flash>>()
        .iter(app.world())
        .map(|(material, tag, pose, no_cull, no_shadow)| {
            (material.0.id(), tag.value, *pose, no_cull, no_shadow)
        })
        .collect();
    assert_eq!(
        flashes.len(),
        3,
        "production flashes use the per-view material"
    );
    assert!(
        flashes
            .iter()
            .all(|(material, tag, pose, no_cull, no_shadow)| {
                *material == flashes[0].0
                    && f32::from_bits(*tag).is_finite()
                    && pose.rotation == Quat::IDENTITY
                    && *no_cull
                    && *no_shadow
            }),
        "one shared material, per-instance roll, no CPU camera basis or flat-AABB culling"
    );
    assert_ne!(
        flashes[0].1, flashes[1].1,
        "accepted flashes retain independent source rolls"
    );
    assert_eq!(
        count::<voices::Voice>(&mut app),
        1,
        "per-gun40ms report cap does not suppress flashes"
    );
    assert_eq!(count::<particles::Emitter>(&mut app), 0);
    send(
        &mut app,
        4,
        json!({"gun":"tank_cannon","muzzle":[1,2,3],"direction":[0,0,-1],"npc":true}),
    );
    app.update();
    assert_eq!(
        count::<particles::Emitter>(&mut app),
        0,
        "source NPC path never requests player-cannon scale2.2"
    );
    assert!(
        app.world_mut()
            .query::<(&Flash, &Transform)>()
            .iter(app.world())
            .all(|(_, p)| p.scale == Vec3::ONE)
    );
    assert_eq!(
        app.world()
            .resource::<Messages<crate::player::flinch::ExplosionFlinch>>()
            .len(),
        0
    );
    let mut effects = app.world_mut().resource_mut::<Effects>();
    assert_eq!(
        clip(
            &mut effects,
            &WorldConfig::default().content_root,
            "absent-mounted-test",
            false
        ),
        Some("nykorev_shoot.ogg".into())
    );
    assert_eq!(
        clip(
            &mut effects,
            &WorldConfig::default().content_root,
            "absent-mounted-test",
            true
        ),
        None
    );
}
