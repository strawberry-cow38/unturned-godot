use super::*;
use crate::avatar::remotes::{
    actions::Actions,
    held::{Held, RemoteMuzzle},
};
use crate::entities::CoreEvent;

fn with_catalog() -> App {
    let mut app = app();
    app.insert_resource(source_catalog());
    app
}
fn equip(app: &mut App, id: u16) {
    let snapshot = &mut app.world_mut().resource_mut::<GameSnapshot>().0;
    snapshot["players"][1]["heldItemId"] = json!(id);
    snapshot["players"][1]["attachments"] = json!({"sightId":21,"magazineId":6,"barrelId":7});
    app.update();
}
fn event(app: &mut App, sequence: u64, kind: &str, data: Value) {
    app.world_mut().write_message(CoreEvent {
        sequence,
        kind: kind.into(),
        data,
    });
    app.update();
}

#[test]
fn accepted_weapon_and_attachment_swaps_use_original_body_hook_and_remove_old_trees() {
    let mut app = with_catalog();
    let body = body(&app);
    equip(&mut app, 4);
    let gun = app.world().get::<Held>(body).unwrap().root.unwrap();
    let rig = app.world().get::<RigPlayback>(body).unwrap();
    assert_eq!(
        app.world().get::<ChildOf>(gun).unwrap().parent(),
        rig.bones["Right_Hook"]
    );
    assert!(
        app.world()
            .get::<Transform>(gun)
            .unwrap()
            .rotation
            .abs_diff_eq(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2), 1e-6)
    );
    assert_eq!(app.world().get::<RemoteMuzzle>(gun).unwrap().player_id, 2);
    let visuals = crate::viewmodel::load_gun_visuals(&WorldConfig::default().content_root).unwrap();
    let visual = &visuals[app
        .world()
        .get::<Held>(body)
        .unwrap()
        .asset
        .as_deref()
        .unwrap()];
    assert_eq!(
        app.world().get::<RemoteMuzzle>(gun).unwrap().point,
        visual.muzzle
    );
    let material = app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(gun)
        .unwrap();
    assert_eq!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(&material.0)
            .unwrap()
            .cull_mode,
        None
    );
    let children: Vec<_> = app.world().get::<Children>(gun).unwrap().iter().collect();
    assert_eq!(children.len(), 3);
    let sight = children
        .iter()
        .find(|e| app.world().get::<Name>(**e).unwrap().as_str() == "Remote Sight attachment 21")
        .unwrap();
    assert_eq!(
        app.world().get::<Transform>(*sight).unwrap().translation,
        visual.sight_position
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["attachments"]["sightId"] =
        json!(22);
    app.update();
    let replacement = app.world().get::<Held>(body).unwrap().root.unwrap();
    assert_ne!(replacement, gun);
    assert!(app.world().get_entity(gun).is_err());
    assert!(children.iter().all(|e| app.world().get_entity(*e).is_err()));
    equip(&mut app, 121);
    let held = app.world().get::<Held>(body).unwrap();
    assert!(!held.gun);
    assert_eq!(held.asset.as_deref(), Some("knife_military"));
    assert!(app.world().get_entity(replacement).is_err());
    let melee = held.root.unwrap();
    equip(&mut app, 0);
    assert!(app.world().get_entity(melee).is_err());
    assert!(app.world().get::<Held>(body).unwrap().root.is_none());
}

#[test]
fn an_equipped_peer_arriving_before_the_catalog_retries_without_an_equipment_change() {
    let mut app = app();
    let root = body(&app);
    app.world_mut().resource_mut::<ItemCatalog>().items.clear();
    equip(&mut app, 4);
    assert!(app.world().get::<Held>(root).unwrap().root.is_none());
    app.insert_resource(source_catalog());
    app.update();
    assert!(app.world().get::<Held>(root).unwrap().root.is_some());
    assert_eq!(body(&app), root);
}

#[test]
fn original_remote_sight_zero_uses_factory_irons_but_magazine_zero_is_empty() {
    let catalog = source_catalog();
    let visuals = crate::viewmodel::load_gun_visuals(&WorldConfig::default().content_root).unwrap();
    let visual = &visuals[catalog.items[&4].asset.as_deref().unwrap()];
    let parts = crate::viewmodel::attachments::body_parts(
        visual,
        &json!({"sightId":0,"magazineId":0,"barrelId":0,"tacticalId":151}),
        &catalog,
    );
    assert_eq!(parts.len(), 1);
    assert_eq!(parts[0].slot, "Sight");
    assert_eq!(Some(&parts[0].mesh), visual.sight.as_ref());
    assert_eq!(parts[0].position, visual.sight_position);
}

#[test]
fn remote_gesture_overlays_change_arms_preserve_legs_and_do_not_replay_retained_facts() {
    let mut app = with_catalog();
    let root = body(&app);
    app.world_mut().resource_mut::<Time<Virtual>>().pause();
    app.update();
    let rig = app.world().get::<RigPlayback>(root).unwrap();
    let arm = rig.bones["Right_Arm"];
    let leg = rig.bones["Left_Leg"];
    let before_arm = *app.world().get::<Transform>(arm).unwrap();
    let before_leg = *app.world().get::<Transform>(leg).unwrap();
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["gesture"] = json!(6);
    app.update();
    assert!(
        !app.world()
            .get::<Transform>(arm)
            .unwrap()
            .rotation
            .abs_diff_eq(before_arm.rotation, 1e-4)
    );
    assert_eq!(*app.world().get::<Transform>(leg).unwrap(), before_leg);
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        Some("Gesture_Surrender")
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["gesture"] = json!(0);
    app.update();
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        None
    );
    app.world_mut().resource_mut::<Time<Virtual>>().unpause();
    event(&mut app, 20, "gesture", json!({"playerId":2,"gesture":9}));
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        Some("Gesture_Wave")
    );
    for _ in 0..100 {
        app.update();
    }
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        None
    );
    event(&mut app, 20, "gesture", json!({"playerId":2,"gesture":9}));
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        None
    );
    event(&mut app, 21, "gesture", json!({"playerId":1,"gesture":9}));
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        None,
        "local owner's event cannot animate a peer"
    );
    event(&mut app, 22, "gesture", json!({"playerId":2,"gesture":9}));
    assert_eq!(
        app.world().get::<Actions>(root).unwrap().gesture.clip(),
        Some("Gesture_Wave")
    );
}

#[test]
fn accepted_melee_uses_own_clips_returns_to_hold_and_unknown_items_never_become_fists() {
    let mut app = with_catalog();
    let root = body(&app);
    equip(&mut app, 121);
    event(&mut app, 1, "melee", json!({"playerId":2,"strong":true}));
    assert_eq!(
        app.world()
            .get::<Actions>(root)
            .unwrap()
            .swing
            .as_ref()
            .unwrap()
            .0,
        "Knife_military_Strong"
    );
    for _ in 0..150 {
        app.update();
    }
    assert!(app.world().get::<Actions>(root).unwrap().swing.is_none());
    event(&mut app, 1, "melee", json!({"playerId":2,"strong":true}));
    assert!(app.world().get::<Actions>(root).unwrap().swing.is_none());
    equip(&mut app, 0);
    event(&mut app, 2, "melee", json!({"playerId":2,"strong":false}));
    assert_eq!(
        app.world()
            .get::<Actions>(root)
            .unwrap()
            .swing
            .as_ref()
            .unwrap()
            .0,
        "Punch_Left"
    );
    equip(&mut app, u16::MAX);
    event(&mut app, 3, "melee", json!({"playerId":2,"strong":false}));
    assert!(app.world().get::<Actions>(root).unwrap().swing.is_none());
    equip(&mut app, 4);
    assert!(app.world().get::<Actions>(root).unwrap().swing.is_none());
    event(&mut app, 4, "melee", json!({"playerId":2,"strong":true}));
    assert!(app.world().get::<Actions>(root).unwrap().swing.is_none());
}
