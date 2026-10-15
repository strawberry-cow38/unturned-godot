use super::*;
use serde_json::json;

mod actions;
mod lights;
mod visual;

fn source_catalog() -> ItemCatalog {
    static CATALOG: std::sync::OnceLock<Value> = std::sync::OnceLock::new();
    let result = CATALOG.get_or_init(|| {
        let bridge =
            crate::bridge::CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))
                .unwrap();
        bridge
            .request(
                "init",
                json!({"contentRoot":WorldConfig::default().content_root}),
            )
            .unwrap();
        bridge.request("catalog", json!({})).unwrap()["result"].clone()
    });
    let items: Vec<crate::entities::ItemVisual> =
        serde_json::from_value(result["items"].clone()).unwrap();
    ItemCatalog {
        items: items.into_iter().map(|item| (item.id, item)).collect(),
        gestures: serde_json::from_value(result["gestures"].clone()).unwrap(),
        ..default()
    }
}

fn row(id: u16, position: Vec3) -> Value {
    json!({"playerId":id,"position":position.to_array(),"yaw":0,"stance":0,
        "alive":true,"profile":{"name":"Remote","face":14},
        "worn":{"shirt":24,"pants":23,"hat":27},"wornQuality":{"shirt":53,"pants":100}})
}
fn app() -> App {
    app_with_physics(false)
}
fn app_with_physics(physics: bool) -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin {
            file_path: WorldConfig::default()
                .content_root
                .canonicalize()
                .unwrap()
                .to_string_lossy()
                .into_owned(),
            ..default()
        },
        TransformPlugin,
        crate::rig::RigPlugin,
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<SkinnedMeshInverseBindposes>()
    .init_resource::<WorldConfig>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<ItemCatalog>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(GameSnapshot(json!({"connected":true,"playerId":1,
            "players":[row(1, Vec3::ZERO),row(2, Vec3::new(2.,0.,0.))],"vehicles":[]})))
    .add_plugins(RemotePlayersPlugin);
    app.world_mut().resource_mut::<ItemCatalog>().items.insert(
        24,
        serde_json::from_value(
            json!({"id":24,"name":"Gold shirt","type":"shirt","hasCondition":true}),
        )
        .unwrap(),
    );
    if physics {
        app.add_plugins(RapierPhysicsPlugin::<NoUserData>::default());
    }
    app.update();
    app.update();
    app
}
fn body(app: &App) -> Entity {
    app.world().resource::<Roster>().roots[&2]
}

#[test]
fn accepted_peers_use_original_rigs_outfit_wear_and_public_layer_without_duplicating_self() {
    let mut app = app();
    let root = body(&app);
    assert_eq!(app.world().resource::<Roster>().roots.len(), 1);
    let rig = app.world().get::<RigPlayback>(root).unwrap();
    assert!(rig.bones.contains_key("Right_Hand"));
    assert!(app.world().get::<crate::rig::RigSkin>(root).is_some());
    assert!(rig.data.anims.contains_key("Move_Crouch"));
    let outfit = app.world().get::<Appearance>(root).unwrap();
    assert_eq!(
        (outfit.shirt, outfit.pants, outfit.face),
        (24, 23, Some(14))
    );
    assert_eq!(outfit.gear, vec![27]);
    assert!((outfit.wear.x - 0.48).abs() < 1e-6);
    assert_eq!(
        app.world().get::<CharacterLayers>(root).unwrap().0,
        RenderLayers::layer(0)
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["wornQuality"]["shirt"] =
        json!(0);
    app.update();
    assert_eq!(body(&app), root, "wear changes keep the same rig");
    assert_eq!(app.world().get::<Appearance>(root).unwrap().wear.x, 1.);
}

#[test]
fn source_glide_speed_stance_and_teleport_are_driven_by_the_replica() {
    let mut app = app();
    let root = body(&app);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["position"] = json!([3, 0, 0]);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["stance"] = json!(2);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["yaw"] = json!(90);
    app.update();
    let pose = app.world().get::<Transform>(root).unwrap();
    assert!((pose.translation.x - (2. + 1. - (-14_f32 * 0.02).exp())).abs() < 1e-5);
    assert!(
        pose.rotation
            .abs_diff_eq(Quat::from_rotation_y(std::f32::consts::FRAC_PI_2), 1e-5)
    );
    assert_eq!(
        app.world().get::<RigPlayback>(root).unwrap().clip,
        "Move_Crouch"
    );
    let player = app.world().get::<RemotePlayer>(root).unwrap();
    let speed = player.speed;
    assert!((player.height - 1.2).abs() < 1e-6);
    let hull = player.hull;
    assert!((app.world().get::<Transform>(hull).unwrap().translation.y - 0.6).abs() < 1e-6);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["position"] = json!([20, 0, 0]);
    app.update();
    assert_eq!(
        app.world().get::<Transform>(root).unwrap().translation.x,
        20.
    );
    assert_eq!(
        app.world().get::<RemotePlayer>(root).unwrap().speed,
        speed,
        "source teleport does not inject a giant speed sample"
    );
}

#[test]
fn seats_disable_the_real_hull_and_leaving_or_loading_retires_the_entire_puppet() {
    let mut app = app();
    let root = body(&app);
    let hull = app.world().get::<RemotePlayer>(root).unwrap().hull;
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["stance"] = json!(4);
    app.update();
    assert!(app.world().get::<ColliderDisabled>(hull).is_some());
    assert_eq!(
        app.world().get::<RigPlayback>(root).unwrap().clip,
        "Idle_Sit"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["stance"] = json!(0);
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"] =
        json!([{"driverPlayerId":9,"passengers":[2]}]);
    app.update();
    assert!(app.world().get::<ColliderDisabled>(hull).is_some());
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"] = json!([]);
    app.update();
    assert!(app.world().get::<ColliderDisabled>(hull).is_none());
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(root).is_err());
    assert!(app.world().get_entity(hull).is_err());
    let replacement = body(&app);
    assert_ne!(replacement, root);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"] = json!([]);
    app.update();
    assert!(app.world().get_entity(replacement).is_err());
    assert!(app.world().resource::<Roster>().roots.is_empty());
}

#[test]
fn remote_capsule_is_solid_to_the_local_player_but_does_not_stop_vehicles() {
    #[derive(Resource, Default)]
    struct Hits([bool; 2]);
    fn probe(context: ReadRapierContext, mut hits: ResMut<Hits>) {
        let Ok(context) = context.single() else {
            return;
        };
        for (index, group) in [Group::GROUP_2, Group::GROUP_4].into_iter().enumerate() {
            hits.0[index] = context
                .cast_ray(
                    Vec3::Y,
                    Vec3::X,
                    4.,
                    true,
                    QueryFilter::default().groups(CollisionGroups::new(group, Group::ALL)),
                )
                .is_some();
        }
    }
    let mut app = app_with_physics(true);
    app.init_resource::<Hits>()
        .add_systems(PostUpdate, probe.after(PhysicsSet::Writeback));
    for _ in 0..3 {
        app.update();
    }
    assert_eq!(app.world().resource::<Hits>().0, [true, false]);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["stance"] = json!(4);
    for _ in 0..3 {
        app.update();
    }
    assert_eq!(app.world().resource::<Hits>().0, [false, false]);
}
