//! The F-chain rank of `RequestEnterNearestPuppet` (PlayerController :8977-9050)
//! through the production registrations: proximity enter, crop harvest and
//! the look-vehicle access scan, each in its installed schedule slot.
use super::*;
use crate::{
    entities::{ApplyInteractions, crops},
    ui::Screen,
};
use crate::{player::PlayerCamera, vehicle::data};
use bevy::time::TimeUpdateStrategy;
use bevy_rapier3d::prelude::*;
use serde_json::Value;

const REMOTE: &str = r#"{"network":{"mode":"client","ownsWorld":false},"player":{"alive":true,"vehicleNetId":0}}"#;

fn snapshot(network: Option<Value>) -> GameSnapshot {
    let mut state: Value = serde_json::from_str(REMOTE).unwrap();
    match network {
        Some(n) => state["network"] = n,
        None => {
            state.as_object_mut().unwrap().remove("network");
        }
    }
    GameSnapshot(state)
}
/// Player at the origin; `look` stands in for one look-target collector
/// (offered exactly as its production system offers it).
fn app(state: GameSnapshot, look: Option<(&'static str, Value, bool)>) -> App {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .insert_resource(state)
        .init_resource::<InteractionFocus>()
        .add_systems(
            Update,
            crate::entities::reset_focus
                .after(crate::player::PlayerUpdate)
                .before(CollectInteractions),
        )
        .add_systems(
            Update,
            (move |mut focus: ResMut<InteractionFocus>| {
                if let Some((action, args, manual)) = &look {
                    if *manual {
                        focus.offer_status(1., "", action, args.clone());
                    } else {
                        focus.offer(1., "target", *action, args.clone(), false);
                    }
                }
            })
            .in_set(CollectInteractions),
        )
        .configure_sets(Update, ApplyInteractions.after(CollectInteractions))
        .add_plugins(ProximityEnterPlugin);
    crops::install_harvest(&mut app);
    app.world_mut().spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::default(),
    ));
    app
}
fn vehicle(app: &mut App, id: u32, at: Vec3) {
    let body = crate::vehicle::tests::body(app, "police", at);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
}
fn crop(app: &mut App, id: u32, at: Vec3) {
    app.world_mut()
        .spawn((crops::CropReplica::for_test(id, true), Transform::from_translation(at)));
}
fn dispatch(app: &App) -> Option<(String, Value)> {
    app.world()
        .resource::<InteractionFocus>()
        .dispatch()
        .map(|(a, v, _)| (a.to_owned(), v.clone()))
}
fn enter(id: u32) -> Option<(String, Value)> {
    Some(("enterVehicle".into(), json!({"netId":id,"seat":255})))
}

#[test]
fn nearest_takes_the_first_strictly_nearest_origin_inside_four_metres() {
    let rows = [(1, Vec3::X * 3.5), (2, Vec3::Z * 2.), (3, Vec3::NEG_Z * 2.), (0, Vec3::X * 0.5)];
    assert_eq!(nearest(Vec3::ZERO, rows.into_iter()), Some(2), "ties keep the first; NetId 0 is no puppet");
    assert_eq!(nearest(Vec3::ZERO, [(4, Vec3::X * 4.)].into_iter()), None, "d² < 16 is strict");
    assert_eq!(nearest(Vec3::ZERO, [(4, Vec3::new(0., 3.9, 0.))].into_iter()), Some(4), "3-D distance");
}

#[test]
fn remote_at_three_point_nine_metres_enters_any_seat_over_crop_and_deployable() {
    let deployable = ("ordinaryDeployable", json!({"netId":7}), true);
    for look in [None, Some(deployable)] {
        let mut app = app(snapshot(Some(json!({"mode":"client","ownsWorld":false}))), look.clone());
        vehicle(&mut app, 40, Vec3::new(3.9, 0., 0.));
        crop(&mut app, 5, Vec3::new(1., 0., 0.));
        app.update();
        let focus = app.world().resource::<InteractionFocus>();
        assert_eq!(dispatch(&app), enter(40), "look={look:?}");
        assert!(focus.preempted() && !focus.idle());
        assert!(!focus.selected("harvestCrop", 5), "harvest is tried after the puppet enter");
        if look.is_some() {
            assert!(!focus.selected("ordinaryDeployable", 7), "the hold-F pickup may not start");
            assert!(focus.looked("ordinaryDeployable", 7), "its billboard and rim stay");
        }
        assert!(
            app.world().resource::<InteractionFocus>().outlined().is_none(),
            "NearestPuppet claims no rim of its own"
        );
    }
}

#[test]
fn remote_at_four_point_one_metres_harvests_instead() {
    let mut app = app(snapshot(Some(json!({"mode":"client","ownsWorld":false}))), None);
    vehicle(&mut app, 40, Vec3::new(4.1, 0., 0.));
    crop(&mut app, 5, Vec3::new(1., 0., 0.));
    app.update();
    assert_eq!(dispatch(&app), Some(("harvestCrop".into(), json!({"netId":5}))));
    assert!(!app.world().resource::<InteractionFocus>().preempted());
}

#[test]
fn an_owning_world_never_offers_proximity_enter() {
    for network in [None, Some(json!({"mode":"local","ownsWorld":true})), Some(json!({"mode":"listen","ownsWorld":true}))] {
        let mut app = app(snapshot(network.clone()), None);
        vehicle(&mut app, 40, Vec3::new(1., 0., 0.));
        app.update();
        assert!(app.world().resource::<InteractionFocus>().idle(), "{network:?}: F inspects");
        crop(&mut app, 5, Vec3::new(1., 0., 0.));
        app.update();
        assert_eq!(dispatch(&app), Some(("harvestCrop".into(), json!({"netId":5}))), "{network:?}");
    }
}

#[test]
fn the_branches_before_it_keep_f_and_the_ones_after_it_lose_it() {
    let remote = || snapshot(Some(json!({"mode":"client","ownsWorld":false})));
    // Stand up, hitch, item and shelf pickup, every looked-at vehicle action.
    for (action, args) in [
        ("sitSeat", json!({"netId":0})),
        ("vehicleHitch", json!({"trailerNetId":9})),
        ("pickupItem", json!({"netId":4})),
        (crate::entities::shelves::TAKE, json!({"netId":4,"slot":0})),
        ("enterVehicle", json!({"netId":9,"seat":1})),
        ("vehicleDoor", json!({"netId":9})),
    ] {
        let mut app = app(remote(), Some((action, args.clone(), false)));
        vehicle(&mut app, 40, Vec3::new(1., 0., 0.));
        app.update();
        assert_eq!(dispatch(&app), Some((action.to_owned(), args)), "{action} ranks above the puppet enter");
    }
    // Manual vehicle zones keep F for their own systems.
    for action in ["openVehicleHood", "openVehicleTrunk", "vehicleDoorMoving"] {
        let mut app = app(remote(), Some((action, json!({"netId":9}), true)));
        vehicle(&mut app, 40, Vec3::new(1., 0., 0.));
        app.update();
        let focus = app.world().resource::<InteractionFocus>();
        assert!(focus.selected(action, 9) && dispatch(&app).is_none(), "{action}");
    }
    // Everything after it in the source chain, by the action and args each
    // production collector really offers (`manual`: offer_status/offer_manual).
    // The source text check keeps a renamed action from passing by default.
    let losers: [(&str, Value, bool, &str); 17] = [
        ("npcTalk", json!({"dialogue":59}), false, include_str!("../../entities/npcs.rs")),
        ("claimBed", json!({"netId":9}), false, include_str!("../../world/beds.rs")),
        ("sitSeat", json!({"netId":9}), false, include_str!("../../world/interactables.rs")),
        ("toggleObjectDoor", json!({"netId":9}), false, include_str!("../../world/interactables.rs")),
        ("openStorage", json!({"netId":9}), false, include_str!("../../world/interactables.rs")),
        ("forageResource", json!({"index":9}), false, include_str!("../../entities.rs")),
        ("toggleDoor", json!({"netId":9}), true, include_str!("../../world/demo_door.rs")),
        ("placedDoor", json!({"netId":9}), true, include_str!("../../deployables/doors.rs")),
        ("ordinaryDeployable", json!({"netId":9}), true, include_str!("../../deployables/interaction.rs")),
        ("toggleLamp", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("sceneryDeployable", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("readNote", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("vend", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("toggleRadio", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("toggleMonitor", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("carLift", json!({"sourceIndex":9}), true, include_str!("../../world/devices.rs")),
        ("harvestCrop", json!({"netId":9}), false, include_str!("../../entities/crops.rs")),
    ];
    for (action, args, manual, source) in losers {
        assert!(source.contains(&format!("\"{action}\"")), "{action} is a real offered action");
        for car in [false, true] {
            let mut app = app(remote(), Some((action, args.clone(), manual)));
            if car {
                vehicle(&mut app, 40, Vec3::new(1., 0., 0.));
            }
            app.update();
            let focus = app.world().resource::<InteractionFocus>();
            if car {
                assert_eq!(dispatch(&app), enter(40), "{action} ranks below the puppet enter");
                assert!(!focus.selected_args(action, &args) && focus.looked_args(action, &args), "{action}");
            } else {
                assert!(focus.selected_args(action, &args), "{action} owns F with no car near");
                let own = (!manual).then(|| (action.to_owned(), args.clone()));
                assert_eq!(dispatch(&app), own, "{action}");
            }
        }
    }
}

/// The production look scan (`access::focus`, real Rapier ray) on a door of
/// one police car beats the proximity enter of a nearer puppet behind you.
#[test]
fn a_looked_at_vehicle_door_still_uses_the_normal_enter() {
    let mut app = app(snapshot(Some(json!({"mode":"client","ownsWorld":false}))), None);
    app.add_plugins((TransformPlugin, RapierPhysicsPlugin::<NoUserData>::default()))
        .insert_resource(TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .insert_resource(TimestepMode::Fixed { dt: 0.02, substeps: 1 })
        .add_systems(
            Update,
            super::super::access::focus
                .in_set(super::super::access::VehicleAccess)
                .after(CollectInteractions)
                .before(ApplyInteractions),
        );
    let looked = crate::vehicle::tests::body(&mut app, "police", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(looked).unwrap().net_id = 10;
    app.world_mut().entity_mut(looked).insert(RigidBody::Fixed);
    let def = data::named("police").unwrap();
    let door = def.access.iter().find(|z| z.kind == "Door").expect("a police door zone");
    let center = Vec3::from_array(door.center);
    let side = Vec3::X * center.x.signum();
    let eye = center + side * 1.6;
    app.world_mut().spawn((PlayerCamera, Transform::from_translation(eye).looking_at(center, Vec3::Y)));
    let feet = eye - Vec3::Y * 1.75;
    *app.world_mut().query_filtered::<&mut Transform, With<PlayerBody>>().single_mut(app.world_mut()).unwrap() =
        Transform::from_translation(feet);
    // A second puppet behind the player, nearer than the looked car and clear
    // of the up-ray below.
    let behind = crate::vehicle::tests::body(&mut app, "police", feet + side * 2.5);
    assert!(feet.distance(feet + side * 2.5) < feet.distance(Vec3::ZERO));
    app.world_mut().get_mut::<VehicleBody>(behind).unwrap().net_id = 11;
    app.world_mut().entity_mut(behind).insert(RigidBody::Fixed).remove::<Collider>();
    app.update();
    app.update();
    app.update();
    let focus = app.world().resource::<InteractionFocus>();
    assert!(!focus.preempted(), "a looked-at vehicle ranks above RequestEnterNearestPuppet");
    assert_eq!(
        dispatch(&app),
        Some(("enterVehicle".into(), json!({"netId":10,"seat":door.seat.max(0)}))),
        "the door zone names its own seat"
    );
    // Look up, away from every hull: the same press now asks for the nearest puppet.
    app.world_mut().query_filtered::<&mut Transform, With<PlayerCamera>>().single_mut(app.world_mut()).unwrap().look_to(Vec3::Y, Vec3::Z);
    app.update();
    assert_eq!(dispatch(&app), enter(11));
}

/// `_focusVehicle` requires `!IsWreck && !IsTrailer`: aiming at the door zone
/// of a husk or a trailer offers nothing, so F falls through to the nearest
/// live car 3 m behind (the looked-at hull is farther). A live police car in
/// the same pose still takes its door (the control).
#[test]
fn a_looked_at_husk_or_trailer_falls_through_to_the_nearest_car() {
    for (key, wreck, falls) in [("police", true, true), ("dinky_trailer", false, true), ("police", false, false)] {
        let mut app = app(snapshot(Some(json!({"mode":"client","ownsWorld":false}))), None);
        app.add_plugins((TransformPlugin, RapierPhysicsPlugin::<NoUserData>::default()))
            .insert_resource(TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
            .insert_resource(TimestepMode::Fixed { dt: 0.02, substeps: 1 })
            .add_systems(
                Update,
                super::super::access::focus
                    .in_set(super::super::access::VehicleAccess)
                    .after(CollectInteractions)
                    .before(ApplyInteractions),
            );
        let looked = crate::vehicle::tests::body(&mut app, key, Vec3::ZERO);
        {
            let mut body = app.world_mut().get_mut::<VehicleBody>(looked).unwrap();
            body.net_id = 10;
            if wreck {
                body.flags |= 32;
            }
        }
        app.world_mut().entity_mut(looked).insert(RigidBody::Fixed);
        let def = data::named(key).unwrap();
        let door = def.access.iter().find(|z| z.kind == "Door").expect("a door zone");
        let center = Vec3::from_array(door.center);
        let side = Vec3::X * center.x.signum();
        let eye = center + side * 2.2;
        app.world_mut().spawn((PlayerCamera, Transform::from_translation(eye).looking_at(center, Vec3::Y)));
        let feet = eye - Vec3::Y * 1.75;
        *app.world_mut().query_filtered::<&mut Transform, With<PlayerBody>>().single_mut(app.world_mut()).unwrap() =
            Transform::from_translation(feet);
        let car = feet + side * 3.;
        assert!(feet.distance(Vec3::ZERO) > 3. && feet.distance(Vec3::ZERO) < REACH, "{key}: the hull is in range but farther");
        let other = crate::vehicle::tests::body(&mut app, "police", car);
        app.world_mut().get_mut::<VehicleBody>(other).unwrap().net_id = 11;
        app.world_mut().entity_mut(other).insert(RigidBody::Fixed).remove::<Collider>();
        for _ in 0..3 {
            app.update();
        }
        if falls {
            assert_eq!(dispatch(&app), enter(11), "{key} wreck={wreck}: F seats in the live car");
        } else {
            assert_eq!(
                dispatch(&app),
                Some(("enterVehicle".into(), json!({"netId":10,"seat":door.seat.max(0)}))),
                "control: a live car's door keeps F"
            );
        }
    }
}

#[test]
fn seated_dead_or_menu_never_offers_it() {
    for state in [
        json!({"network":{"ownsWorld":false},"player":{"alive":true,"vehicleNetId":3}}),
        json!({"network":{"ownsWorld":false},"player":{"alive":false,"vehicleNetId":0}}),
    ] {
        let mut app = app(GameSnapshot(state.clone()), None);
        vehicle(&mut app, 40, Vec3::new(1., 0., 0.));
        app.update();
        assert!(app.world().resource::<InteractionFocus>().idle(), "{state}");
    }
    let mut app = app(snapshot(Some(json!({"ownsWorld":false}))), None);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    vehicle(&mut app, 40, Vec3::new(1., 0., 0.));
    app.update();
    assert!(app.world().resource::<InteractionFocus>().idle());
}
