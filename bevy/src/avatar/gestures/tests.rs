use super::*;
use crate::{bridge::CoreBridge, world::WorldConfig};
use std::{path::Path, sync::OnceLock};

fn metadata() -> Vec<Definition> {
    static DATA: OnceLock<Vec<Definition>> = OnceLock::new();
    DATA.get_or_init(|| {
        let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
        bridge
            .request(
                "init",
                json!({"contentRoot":WorldConfig::default().content_root}),
            )
            .unwrap();
        let result = bridge.request("catalog", json!({})).unwrap();
        serde_json::from_value(result["result"]["gestures"].clone())
            .expect("actual core GestureRules metadata")
    })
    .clone()
}
fn rig() -> RigData {
    RigData::load(&WorldConfig::default().content_root.join("rig.json")).unwrap()
}

#[test]
fn source_catalog_drives_selectors_and_null_clip_does_not_invent_a_pose_or_cuff_escape() {
    let catalog = metadata();
    assert_eq!(catalog.len(), 18);
    assert_eq!(selected(GameAction::Surrender, 0, &catalog), Some(6));
    assert_eq!(selected(GameAction::Surrender, 6, &catalog), Some(7));
    assert_eq!(selected(GameAction::GestureRest, 13, &catalog), Some(14));
    assert_eq!(definition(&catalog, 16).unwrap().clip, None);
    assert_eq!(
        definition(&catalog, 13).unwrap().required_stance.as_deref(),
        Some("crouch")
    );
    assert!(!definition(&catalog, 11).unwrap().requestable);
    let mut model = UiModel::default();
    assert_eq!(console("give 4", 0, &catalog, &mut model), None);
    assert_eq!(
        console("gesture stop", 11, &catalog, &mut model),
        Some(Some(12)),
        "request still goes through authority's arrest refusal"
    );
    assert_eq!(
        console("gesture rest", 0, &catalog, &mut model),
        Some(Some(13))
    );
    assert_eq!(console("gesture", 6, &catalog, &mut model), Some(None));
    assert_eq!(
        console("gesture nonsense", 0, &catalog, &mut model),
        Some(None)
    );
    let mut playback = Playback::default();
    playback.adopt(16, &catalog, &rig());
    assert_eq!(
        playback.clip(),
        None,
        "source T-pose has no animation implementation"
    );
}
#[test]
fn actual_key_route_requests_without_posing_and_modal_consumption_wins() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .insert_resource({
            let mut model = UiModel::default();
            model.connected = true;
            model.alive = true;
            model
        })
        .insert_resource(GameSnapshot(json!({"player":{"gesture":0}})))
        .insert_resource(ItemCatalog {
            gestures: metadata(),
            ..default()
        })
        .add_message::<UiIntent>()
        .add_systems(Update, controls);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyB);
    app.update();
    let messages: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect();
    assert!(
        matches!(&messages[..],[UiIntent::Action{name,args}] if name=="requestGesture" && args["gesture"]==6)
    );
    assert_eq!(
        app.world().resource::<GameSnapshot>().0["player"]["gesture"],
        0
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Console;
    app.update();
    assert_eq!(
        app.world_mut()
            .resource_mut::<Messages<UiIntent>>()
            .drain()
            .count(),
        0
    );
}
#[test]
fn source_one_shot_restarts_finishes_and_preserves_loop_state_edge() {
    let catalog = metadata();
    let rig = rig();
    let mut playback = Playback::default();
    playback.adopt(0, &catalog, &rig);
    playback.play(9, &catalog, &rig);
    playback.advance(0.6, &catalog, &rig);
    playback.play(9, &catalog, &rig);
    assert_eq!(playback.active.as_ref().unwrap().time, 0.);
    playback.advance(1.31, &catalog, &rig);
    assert_eq!(playback.clip(), None);
    playback.adopt(6, &catalog, &rig);
    playback.advance(50., &catalog, &rig);
    assert_eq!(playback.clip(), Some("Gesture_Surrender"));
    playback.play(9, &catalog, &rig);
    playback.advance(2., &catalog, &rig);
    assert_eq!(playback.accepted, Some(6));
    assert_eq!(
        playback.active.as_ref().unwrap().time,
        rig.anims["Gesture_Wave"].length
    );
    playback.adopt(0, &catalog, &rig);
    assert_eq!(playback.clip(), None);
}
#[test]
fn accepted_replica_changes_real_arms_but_preserves_locomotion_and_retained_events_do_not_restart()
{
    let mut app = crate::avatar::tests::avatar_app();
    app.world_mut().resource_mut::<ItemCatalog>().gestures = metadata();
    app.world_mut().resource_mut::<Time<Virtual>>().pause();
    app.world_mut().resource_mut::<GameSnapshot>().0["playerId"] = json!(1);
    app.update();
    let root = app
        .world()
        .resource::<crate::avatar::AvatarState>()
        .root
        .unwrap();
    let rig = app.world().get::<RigPlayback>(root).unwrap();
    let arm = rig.bones["Right_Arm"];
    let leg = rig.bones["Left_Leg"];
    let before = app.world().get::<Transform>(arm).unwrap().rotation;
    let leg_before = *app.world().get::<Transform>(leg).unwrap();
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["gesture"] = json!(6);
    app.update();
    assert!(before.angle_between(app.world().get::<Transform>(arm).unwrap().rotation) > 0.1);
    assert_eq!(*app.world().get::<Transform>(leg).unwrap(), leg_before);
    assert_eq!(
        app.world().resource::<Local>().playback.clip(),
        Some("Gesture_Surrender")
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["gesture"] = json!(0);
    let event = CoreEvent {
        sequence: 20,
        kind: "gesture".into(),
        data: json!({"playerId":1,"gesture":9}),
    };
    app.world_mut().write_message(event.clone());
    app.update();
    app.world_mut()
        .resource_mut::<Local>()
        .playback
        .active
        .as_mut()
        .unwrap()
        .time = 0.8;
    app.world_mut().write_message(event);
    app.update();
    assert_eq!(
        app.world()
            .resource::<Local>()
            .playback
            .active
            .as_ref()
            .unwrap()
            .time,
        0.8
    );
    app.world_mut().resource_mut::<UiModel>().alive = false;
    app.update();
    assert_eq!(app.world().resource::<Local>().playback.clip(), None);
}

#[test]
fn real_surrender_key_waits_for_managed_acceptance_then_changes_the_original_body() {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root}),
        )
        .unwrap();
    let mut app = crate::avatar::tests::avatar_app();
    app.world_mut().resource_mut::<ItemCatalog>().gestures = metadata();
    app.world_mut().resource_mut::<Time<Virtual>>().pause();
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        bridge.request("snapshot", json!({})).unwrap()["state"].clone();
    app.update();
    let root = app
        .world()
        .resource::<crate::avatar::AvatarState>()
        .root
        .unwrap();
    let arm = app.world().get::<RigPlayback>(root).unwrap().bones["Right_Arm"];
    let before = app.world().get::<Transform>(arm).unwrap().rotation;
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyB);
    app.update();
    assert_eq!(app.world().resource::<Local>().playback.clip(), None);
    let intents: Vec<_> = app
        .world_mut()
        .resource_mut::<Messages<UiIntent>>()
        .drain()
        .collect();
    assert_eq!(intents.len(), 1);
    let UiIntent::Action { name, args } = &intents[0] else {
        panic!("gesture must use existing action transport")
    };
    let response = bridge
        .request("action", json!({"name":name,"args":args}))
        .unwrap();
    assert_eq!(response["result"]["queued"], true);
    assert_eq!(
        response["state"]["player"]["gesture"], 0,
        "request is not acceptance"
    );
    let mut state = serde_json::Value::Null;
    for _ in 0..12 {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    assert_eq!(state["player"]["gesture"], 6);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .reset_all();
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.update();
    assert_eq!(
        app.world().resource::<Local>().playback.clip(),
        Some("Gesture_Surrender")
    );
    assert!(before.angle_between(app.world().get::<Transform>(arm).unwrap().rotation) > 0.1);
    // Stop traverses the same source rule and published state, never a local
    // blanket clear that could release another player's cuffs.
    let stop = selected(GameAction::Surrender, 6, &metadata()).unwrap();
    bridge
        .request(
            "action",
            json!({"name":"requestGesture","args":{"gesture":stop}}),
        )
        .unwrap();
    for _ in 0..12 {
        app.world_mut().resource_mut::<GameSnapshot>().0 =
            bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    app.update();
    assert_eq!(app.world().resource::<Local>().playback.clip(), None);
}
