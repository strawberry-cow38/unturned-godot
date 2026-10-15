use super::*;
use bevy::ecs::system::SystemState;

fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        crate::rig::RigPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ));
    app.init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .init_asset::<SkinnedMeshInverseBindposes>()
        .init_resource::<Wildlife>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<WorldConfig>()
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .insert_resource(GameSnapshot(json!({"animals":[]})))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .add_systems(Update, sync.before(RigAnimation));
    app
}
fn animal(name: &str, id: u32, height: f32) -> serde_json::Value {
    json!({"netId":id,"species":0,"position":[0,0,0],"yaw":0,"clip":"Walk","dead":false,
        "bodyHeight":height,"radius":(height*0.38).clamp(0.25,0.7),"rig":format!("{name}_rig.json"),
        "texture":format!("objects/Animal_{}_tex.png",format!("{}{}",name[..1].to_uppercase(),&name[1..])),
        "rigYawFix":270})
}
fn hit(app: &mut App, at: Vec3) -> bool {
    let mut system = SystemState::<ReadRapierContext>::new(app.world_mut());
    let ctx = system.get(app.world()).unwrap();
    ctx.single()
        .unwrap()
        .cast_ray(
            at,
            Vec3::NEG_Z,
            5.,
            true,
            QueryFilter::default().groups(CollisionGroups::new(Group::GROUP_2, Group::GROUP_5)),
        )
        .is_some()
}
#[test]
fn accepted_pose_blocks_player_then_death_freezes_skin_and_disables_capsule() {
    let mut app = app();
    let mut row = animal("deer", 17, 1.3);
    row["position"] = json!([10, 2, 5]);
    row["hostPosition"] = json!([10.0123, 2.002, 5.005]);
    row["hostYaw"] = json!(90.0);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"animals":[row.clone()]});
    for _ in 0..5 {
        app.update();
    }
    let entity = app.world().resource::<Wildlife>().entities[&17];
    let body = app.world().get::<AnimalBody>(entity).unwrap();
    let rig = body.rig;
    let bone = app.world().get::<RigPlayback>(rig).unwrap().bones["Left_Back"];
    let pose = *app.world().get::<Transform>(entity).unwrap();
    assert!(pose.translation.distance(Vec3::new(10.0123, 2.002, 5.005)) < 1e-5);
    assert!((pose.rotation * Vec3::NEG_Z).distance(Vec3::NEG_X) < 1e-5);
    let before = *app.world().get::<Transform>(bone).unwrap();
    for _ in 0..8 {
        app.update();
    }
    assert_ne!(
        before,
        *app.world().get::<Transform>(bone).unwrap(),
        "source Walk track must really animate"
    );
    assert!(
        hit(&mut app, Vec3::new(10., 2.6, 8.)),
        "accepted living capsule blocks the player's query mask"
    );
    row["dead"] = json!(true);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"animals":[row]});
    app.update();
    app.update();
    let before = *app.world().get::<Transform>(bone).unwrap();
    for _ in 0..8 {
        app.update();
    }
    assert_eq!(before, *app.world().get::<Transform>(bone).unwrap());
    assert!(app.world().get::<RigPlayback>(rig).unwrap().paused);
    assert!(
        !hit(&mut app, Vec3::new(10., 2.6, 8.)),
        "dead animal must not remain a live hit capsule"
    );
    // Cleanup follows server retirement rather than an independent local timer.
    assert!(app.world().get_entity(entity).is_ok());
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"animals":[]});
    app.update();
    assert!(app.world().get_entity(entity).is_err());
    assert!(app.world().get_entity(rig).is_err());
    assert!(app.world().get_entity(bone).is_err());
}
#[test]
fn source_fleet_assets_animate_and_load_without_invented_ragdoll_metadata() {
    let mut app = app();
    let rows = [("deer", 1.3), ("pig", 0.75), ("cow", 1.45), ("horse", 1.81)]
        .into_iter()
        .enumerate()
        .map(|(i, (name, h))| animal(name, i as u32 + 1, h))
        .collect::<Vec<_>>();
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"animals":rows});
    app.update();
    app.update();
    assert_eq!(app.world().resource::<Wildlife>().entities.len(), 4);
    for (_, entity) in app.world().resource::<Wildlife>().entities.iter() {
        let body = app.world().get::<AnimalBody>(*entity).unwrap();
        let playback = app.world().get::<RigPlayback>(body.rig).unwrap();
        assert_eq!(playback.bones.len(), 7);
        assert_eq!(playback.clip, "Walk");
        assert!(playback.data.anims.contains_key("Run"));
        let visual = app.world().get::<Transform>(body.rig).unwrap();
        assert!(
            (visual.rotation * Vec3::NEG_X).distance(Vec3::NEG_Z) < 1e-5,
            "source model's -X must face travel -Z"
        );
    }
    let root = WorldConfig::default().content_root;
    for name in ["deer", "pig", "cow", "horse"] {
        let data: serde_json::Value =
            serde_json::from_slice(&std::fs::read(root.join(format!("{name}_rig.json"))).unwrap())
                .unwrap();
        assert!(
            data["ragdoll"].is_null() || data["ragdoll"].as_object().is_some_and(|v| v.is_empty()),
            "Ragdoll asset data now exists: implement source physical bones instead of freezing corpses"
        );
    }
}
#[test]
fn source_panic_banks_are_silent_when_absent_and_never_repeat() {
    let root = WorldConfig::default().content_root;
    for name in ["deer", "horse"] {
        assert!(PanicBank::load(&root, name).pick(0).is_none());
    }
    for name in ["pig", "cow"] {
        let mut bank = PanicBank::load(&root, name);
        assert_eq!(bank.clips.len(), 3);
        let mut last = String::new();
        for seed in 0..30 {
            let path = bank.pick(seed).unwrap();
            assert_ne!(last, path);
            last = path.into();
        }
    }
}
#[test]
fn load_epoch_replaces_body_once_and_loaded_corpse_never_plays_locomotion() {
    let mut app = app();
    let mut row = animal("pig", 8, 0.75);
    row["dead"] = json!(true);
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"animals":[row]});
    app.update();
    app.update();
    let old = app.world().resource::<Wildlife>().entities[&8];
    let rig = app.world().get::<AnimalBody>(old).unwrap().rig;
    assert_eq!(app.world().get::<RigPlayback>(rig).unwrap().time, 0.);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    let new = app.world().resource::<Wildlife>().entities[&8];
    assert_ne!(old, new);
    assert!(app.world().get_entity(old).is_err());
    app.update();
    assert_eq!(new, app.world().resource::<Wildlife>().entities[&8]);
}

#[test]
fn retained_panic_plays_once_even_after_animal_replica_retired() {
    let mut app = app();
    app.init_asset::<AudioSource>()
        .init_resource::<Settings>()
        .add_message::<CoreEvent>()
        .add_systems(Update, panic_audio.after(sync));
    let event = CoreEvent {
        sequence: 8,
        kind: "animalPanic".into(),
        data: json!({"netId":2,"species":2,"rig":"cow","position":{"x":3,"y":4,"z":5}}),
    };
    app.world_mut().write_message(event.clone());
    app.update();
    let voices = app
        .world_mut()
        .query::<(&AudioPlayer, &Transform)>()
        .iter(app.world())
        .map(|(_, pose)| pose.translation)
        .collect::<Vec<_>>();
    assert_eq!(
        voices,
        vec![Vec3::new(3., 4., 5.)],
        "retained event carries audio identity after snapshot retirement"
    );
    app.world_mut().write_message(event);
    app.update();
    assert_eq!(
        app.world_mut()
            .query::<&AudioPlayer>()
            .iter(app.world())
            .count(),
        1,
        "retained event cannot replay sound twice"
    );
}
