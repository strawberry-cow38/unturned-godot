use super::*;
use serde_json::json;
pub(super) fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin {
        file_path: WorldConfig::default().content_root.canonicalize().unwrap().to_string_lossy().into(),
        ..default()
    }, TransformPlugin, crate::rig::RigPlugin))
        .init_asset::<Mesh>().init_asset::<Image>().init_asset::<StandardMaterial>()
        .init_asset::<SkinnedMeshInverseBindposes>()
        .init_resource::<WorldConfig>().init_resource::<WorldSessionEpoch>()
        .insert_resource(crate::avatar::remotes::tests::source_catalog())
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .insert_resource(GameSnapshot(json!({"connected":true,"player":{
            "heldItemId":0,"stance":"prone","position":[500,90,-500],"profile":{"face":19}},
            "inventory":{"worn":{"shirt":24,"pants":23,"hat":27},"wornQuality":{"shirt":25,"pants":100}},
            "weapon":{"attachments":{"sightId":21,"barrelId":7,"magazineId":6}}})))
        .add_plugins(PaperdollPlugin);
    app.world_mut().resource_mut::<Portrait>().active = true;
    app.update();
    app.update();
    app
}
#[test]
fn independent_portrait_uses_owner_outfit_standing_hold_and_exact_attachment_changes() {
    let mut app = app();
    let root = app.world().resource::<Portrait>().root.unwrap();
    assert!(app.world().get::<crate::player::PlayerBody>(root).is_none());
    assert!(
        app.world()
            .get::<bevy_rapier3d::prelude::RigidBody>(root)
            .is_none()
    );
    let outfit = app.world().get::<Appearance>(root).unwrap();
    assert_eq!(
        (outfit.shirt, outfit.pants, outfit.face),
        (24, 23, Some(19))
    );
    assert_eq!(outfit.gear, vec![27]);
    assert!((outfit.wear.x - 0.76).abs() < 0.001);
    assert_eq!(
        app.world().get::<CharacterLayers>(root).unwrap().0,
        RenderLayers::layer(LAYER)
    );
    let rig = app.world().get::<RigPlayback>(root).unwrap();
    assert_eq!(rig.clip, "Idle_Stand");
    let arm = rig.bones["Right_Arm"];
    let legs = [rig.bones["Left_Leg"], rig.bones["Right_Leg"]];
    let before = *app.world().get::<Transform>(arm).unwrap();
    let lower = legs.map(|e| *app.world().get::<Transform>(e).unwrap());
    app.world_mut().resource_mut::<Time<Virtual>>().pause();
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["heldItemId"] = json!(4);
    app.update();
    let held = app.world().get::<Held>(root).unwrap();
    let gun = held.root.unwrap();
    assert_eq!(
        app.world().get::<ChildOf>(gun).unwrap().parent(),
        app.world().get::<RigPlayback>(root).unwrap().bones["Right_Hook"]
    );
    assert!(
        !app.world()
            .get::<Transform>(arm)
            .unwrap()
            .rotation
            .abs_diff_eq(before.rotation, 1e-4)
    );
    assert_eq!(
        legs.map(|e| *app.world().get::<Transform>(e).unwrap()),
        lower
    );
    assert_eq!(
        app.world().get::<RigPlayback>(root).unwrap().clip,
        "Idle_Stand"
    );
    assert_eq!(
        app.world().get::<Transform>(root).unwrap().translation,
        Vec3::ZERO
    );
    assert_eq!(
        *app.world().get::<RenderLayers>(gun).unwrap(),
        RenderLayers::layer(LAYER)
    );
    let children: Vec<_> = app.world().get::<Children>(gun).unwrap().iter().collect();
    assert_eq!(children.len(), 3);
    assert!(
        children
            .iter()
            .all(|e| *app.world().get::<RenderLayers>(*e).unwrap() == RenderLayers::layer(LAYER))
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["attachments"]["sightId"] =
        json!(22);
    app.world_mut().resource_mut::<Portrait>().yaw = 0.6;
    app.update();
    assert!(app.world().get_entity(gun).is_err());
    assert!(children.iter().all(|e| app.world().get_entity(*e).is_err()));
    assert!(
        app.world()
            .get::<Transform>(root)
            .unwrap()
            .rotation
            .abs_diff_eq(Quat::from_rotation_y(std::f32::consts::PI + 0.6), 1e-6)
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["heldItemId"] = json!(121);
    app.update();
    assert_eq!(
        app.world().get::<Held>(root).unwrap().asset.as_deref(),
        Some("knife_military")
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["heldItemId"] = json!(0);
    app.update();
    assert!(app.world().get::<Held>(root).unwrap().root.is_none());
    app.world_mut().resource_mut::<Portrait>().active = false;
    app.update();
    assert_eq!(
        *app.world().get::<Visibility>(root).unwrap(),
        Visibility::Hidden
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["connected"] = json!(false);
    app.update();
    assert!(app.world().get_entity(root).is_err());
}
