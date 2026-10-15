use super::super::*;
use super::*;
use crate::entities::{InteractionFocus, ItemVisual};
use bevy::time::TimeUpdateStrategy;
use serde_json::json;
use std::{
    sync::{Arc, OnceLock},
    time::Duration,
};

fn rig() -> RigPlayback {
    static DATA: OnceLock<Arc<crate::rig::RigData>> = OnceLock::new();
    let data = DATA
        .get_or_init(|| {
            Arc::new(
                crate::rig::RigData::load(
                    &Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content/rig.json"),
                )
                .unwrap(),
            )
        })
        .clone();
    RigPlayback {
        data,
        bones: default(),
        clip: "Eaglefire_Equip".into(),
        time: 2.,
        speed: 1.,
        looping: false,
        paused: false,
        overlay: None,
        additive: None,
    }
}

fn app(asset: &str, gun: bool) -> (App, Entity) {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(TimeUpdateStrategy::ManualDuration(Duration::from_secs_f32(
            0.1,
        )))
        .init_resource::<ViewmodelStatus>()
        .init_resource::<crate::use_items::HandUse>()
        .init_resource::<crate::recoil::Recoil>()
        .init_resource::<crate::vehicle::Occupancy>()
        .init_resource::<InteractionFocus>()
        .init_resource::<crate::player::WorldSessionEpoch>()
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<CoreEvent>()
        .add_message::<crate::use_items::HandEvent>()
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .insert_resource(GameSnapshot(json!({"connected":true,"playerId":1,
            "player":{"alive":true,"heldItemId":4,"stance":"stand"},"weapon":{"firemode":0}})))
        .add_systems(Update, (reset_after_session, animate_viewmodel).chain());
    let item: ItemVisual = serde_json::from_value(json!({"id":4,"name":asset,
        "asset":asset,"type":if gun {"gun"} else {"melee"},
        "gun":if gun {json!({})} else {json!(null)},
        "melee":if gun {json!(null)} else {json!({})}}))
    .unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items.insert(4, item);
    app.insert_resource(catalog);
    let entity = app.world_mut().spawn(rig()).id();
    app.insert_resource(ViewmodelState {
        arms: Some(entity),
        held: Some(4),
        asset: asset.into(),
        is_gun: gun,
        hold_clip: "Eaglefire_Equip".into(),
        equip_elapsed: 100.,
        ..default()
    });
    app.update();
    (app, entity)
}

fn press_interact(app: &mut App) {
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::KeyF);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
}

#[test]
fn only_original_weapon_clips_and_explicit_sks_donor_are_eligible() {
    let rig = rig();
    assert_eq!(
        clip(&rig, "eaglefire", true, false).as_deref(),
        Some("Eaglefire_Inspect")
    );
    assert_eq!(
        clip(&rig, "sks", true, false).as_deref(),
        Some("Zubeknakov_Inspect")
    );
    assert_eq!(
        clip(&rig, "katana", false, true).as_deref(),
        Some("Katana_Inspect")
    );
    assert_eq!(
        clip(&rig, "mac10", true, false),
        None,
        "equip donor must not become an inspect donor"
    );
    assert_eq!(
        clip(&rig, "eaglefire", false, false),
        None,
        "holdables never borrow a gun's inspect"
    );
}

#[test]
fn focus_has_priority_and_safety_pose_yields_to_timed_inspection() {
    let (mut app, entity) = app("eaglefire", true);
    assert!(
        app.world().resource::<ViewmodelState>().sprinting,
        "safety lowered pose active"
    );
    app.world_mut().resource_mut::<InteractionFocus>().offer(
        1.,
        "Pick up",
        "pickup",
        json!({}),
        false,
    );
    press_interact(&mut app);
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    press_interact(&mut app);
    assert!(app.world().resource::<ViewmodelStatus>().inspecting);
    assert_eq!(
        app.world().get::<RigPlayback>(entity).unwrap().clip,
        "Eaglefire_Inspect"
    );
    assert!(!app.world().resource::<ViewmodelState>().sprinting);
    for _ in 0..35 {
        app.update();
    }
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    assert!(app.world().resource::<ViewmodelState>().sprinting);
}

#[test]
fn gun_cancel_consumes_click_and_aim_or_attachment_replaces_inspect_pose() {
    let (mut app, entity) = app("eaglefire", true);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["firemode"] = json!(1);
    press_interact(&mut app);
    assert!(
        app.world_mut()
            .resource_mut::<ViewmodelStatus>()
            .cancel_inspect_from_fire()
    );
    app.update();
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    let rig = app.world().get::<RigPlayback>(entity).unwrap();
    assert_eq!(rig.clip, "Eaglefire_Equip");
    assert_eq!(
        rig.time, rig.data.anims[&rig.clip].length,
        "snap to ready, no re-equip replay"
    );
    assert!(
        !app.world_mut()
            .resource_mut::<ViewmodelStatus>()
            .cancel_inspect_from_fire()
    );
    press_interact(&mut app);
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Right);
    app.update();
    assert!(app.world().resource::<ViewmodelStatus>().aiming);
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .reset_all();
    press_interact(&mut app);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Attachments;
    app.update();
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    assert_eq!(
        app.world().get::<RigPlayback>(entity).unwrap().clip,
        "Eaglefire_AttachStart"
    );
}

#[test]
fn actual_sprint_cancels_both_melee_and_gun_but_stationary_sprint_does_not() {
    for (asset, gun) in [("eaglefire", true), ("katana", false)] {
        let (mut app, entity) = app(asset, gun);
        app.world_mut().resource_mut::<GameSnapshot>().0["player"]["stance"] = json!("sprint");
        press_interact(&mut app);
        assert!(
            app.world().resource::<ViewmodelStatus>().inspecting,
            "{asset}: stationary"
        );
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .press(KeyCode::KeyW);
        app.update();
        assert!(
            !app.world().resource::<ViewmodelStatus>().inspecting,
            "{asset}: moving"
        );
        assert!(
            !app.world()
                .get::<RigPlayback>(entity)
                .unwrap()
                .clip
                .ends_with("_Inspect")
        );
        press_interact(&mut app);
        assert!(
            !app.world().resource::<ViewmodelStatus>().inspecting,
            "{asset}: cannot restart at a run"
        );
    }
}

#[test]
fn inspect_laser_tracks_rotated_model_positive_y() {
    let rotation = Quat::from_rotation_z(1.2) * Quat::from_rotation_x(0.7);
    let origin = Vec3::new(11., 3., -5.);
    let (from, direction) = laser_ray(Mat4::from_scale_rotation_translation(
        Vec3::splat(2.),
        rotation,
        origin,
    ));
    assert_eq!(from, origin);
    assert!(direction.abs_diff_eq(rotation * Vec3::Y, 1e-6));
    assert!((direction.length() - 1.).abs() < 1e-6);
    assert!(direction.dot(rotation * Vec3::NEG_Z).abs() < 1e-6);
}

#[test]
fn reload_blocks_start_and_world_load_clears_an_existing_gesture() {
    let (mut app, entity) = app("eaglefire", true);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["reloading"] = json!(true);
    press_interact(&mut app);
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["reloading"] = json!(false);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["firemode"] = json!(1);
    press_interact(&mut app);
    assert!(app.world().resource::<ViewmodelStatus>().inspecting);
    app.world_mut()
        .resource_mut::<crate::player::WorldSessionEpoch>()
        .0 += 1;
    app.update();
    assert!(!app.world().resource::<ViewmodelStatus>().inspecting);
    assert_eq!(
        app.world().get::<RigPlayback>(entity).unwrap().clip,
        "Eaglefire_Equip"
    );
}
