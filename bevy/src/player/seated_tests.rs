//! Actual native fixed-step intents through the managed authority, with one body.
use super::*;
use crate::vehicle::{Occupancy, OccupiedSeat};

fn tick(bridge: &CoreBridge, count: usize) -> Value {
    let mut state = Value::Null;
    for _ in 0..count {
        state = bridge.request("tick", json!({})).unwrap()["state"].clone();
    }
    state
}
fn fixture(vehicle: bool) -> (App, Entity) {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
    bridge
        .request(
            "init",
            json!({"contentRoot":root,"starterKit":true,"spawn":[0,0,0]}),
        )
        .unwrap();
    let mut catalog = crate::entities::ItemCatalog::default();
    for row in bridge.request("catalog", json!({})).unwrap()["result"]["items"]
        .as_array()
        .unwrap()
    {
        let item: crate::entities::ItemVisual = serde_json::from_value(row.clone()).unwrap();
        catalog.items.insert(item.id, item);
    }
    let net_id = if vehicle {
        let id=bridge.request("worldVehicles", json!({"vehicles":[{"typeId":1,"key":"seated-test","position":[0,0,0],"health":300,"fuel":10,"seatOffsets":[[0,0,0],[0,0,0]]}]})).unwrap()["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
        bridge
            .request(
                "action",
                json!({"name":"enterVehicle","args":{"netId":id,"seat":1}}),
            )
            .unwrap();
        id
    } else {
        bridge
            .request(
                "worldServices",
                json!({"seats":[{"netId":900,"position":[0,0,0]}]}),
            )
            .unwrap();
        bridge
            .request("action", json!({"name":"sitSeat","args":{"netId":900}}))
            .unwrap();
        900
    };
    let snapshot = tick(&bridge, 12);
    if vehicle {
        assert_eq!(snapshot["player"]["vehicleSeat"], 1);
    } else {
        assert_eq!(
            snapshot["interactables"]["seats"][0]["occupant"],
            snapshot["playerId"]
        );
    }
    let mut model = UiModel::default();
    model.adopt_snapshot(&snapshot);
    let mut viewmodel = crate::viewmodel::ViewmodelStatus::default();
    viewmodel.held = 4; viewmodel.ready = true;
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .insert_resource(catalog)
        .insert_resource(model)
        .insert_resource(GameSnapshot(snapshot))
        .insert_resource(Session {
            initialized: true,
            ..default()
        })
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .insert_resource(PlayerInput {
            held_item_id: 4,
            fire: true,
            fire_pressed: true,
            in_seat: true,
            ..default()
        })
        .insert_resource(viewmodel)
        .init_resource::<WorldState>()
        .init_resource::<crate::weapons::Trigger>()
        .init_resource::<crate::use_items::HandUse>()
        .init_resource::<crate::avatar::Perspective>()
        .init_resource::<Occupancy>()
        .init_resource::<crate::world::interactables::PropOccupancy>()
        .add_message::<crate::use_items::HandEvent>()
        .add_systems(Update, simulate);
    let body = app
        .world_mut()
        .spawn((
            Transform::IDENTITY,
            PlayerBody {
                velocity: Vec3::ZERO,
                grounded: false,
                height: 2.,
                stance: "sitting".into(),
                eye_height: 1.75,
                locomotion: default(),
            },
            capsule(2.),
            KinematicCharacterController {
                translation: Some(Vec3::X),
                ..default()
            },
        ))
        .id();
    let camera = Transform::from_xyz(0., 1., 0.).looking_at(Vec3::new(10., 1., 0.), Vec3::Y);
    app.world_mut().spawn((PlayerCamera, camera));
    if vehicle {
        app.world_mut().resource_mut::<Occupancy>().seat = Some(OccupiedSeat {
            vehicle: body,
            net_id,
            seat: 1,
            body_pose: Transform::IDENTITY,
            eye_pose: camera,
            velocity: Vec3::ZERO,
            driver: false,
            aircraft: false,
        });
    } else {
        app.world_mut()
            .resource_mut::<crate::world::interactables::PropOccupancy>()
            .seat = Some(crate::world::interactables::OccupiedPropSeat {
            net_id,
            body_pose: Transform::IDENTITY,
            eye_pose: camera,
            anchor: Vec3::ZERO,
            exit_position: Vec3::Z,
        });
    }
    (app, body)
}
#[test]
fn passenger_fire_uses_seated_camera_without_motion_and_blocks_driver_or_third_person() {
    let (mut app, body) = fixture(true);
    app.update();
    let state = tick(app.world().resource::<CoreBridge>(), 12);
    assert_eq!(
        state["player"]["ammo"], 29,
        "native seated input must reach original ServerCombat"
    );
    let shot = state["events"]
        .as_array()
        .unwrap()
        .iter()
        .find(|e| e["kind"] == "fire")
        .unwrap();
    assert!(
        shot["data"]["dir"]["x"].as_f64().unwrap() > 0.99,
        "actual seated gaze, not owner's zero yaw"
    );
    assert_eq!(
        app.world().get::<Transform>(body).unwrap().translation,
        Vec3::ZERO
    );
    assert!(
        app.world()
            .get::<KinematicCharacterController>(body)
            .unwrap()
            .translation
            .is_none()
    );
    for third_person in [true, false] {
        app.world_mut()
            .resource_mut::<crate::avatar::Perspective>()
            .first_person = !third_person;
        app.world_mut()
            .resource_mut::<Occupancy>()
            .seat
            .as_mut()
            .unwrap()
            .driver = !third_person;
        app.world_mut().resource_mut::<PlayerInput>().fire_pressed = true;
        app.update();
        assert_eq!(
            tick(app.world().resource::<CoreBridge>(), 12)["player"]["ammo"],
            29
        );
    }
}
#[test]
fn prop_seat_can_fire_and_melee_while_its_only_body_stays_fixed() {
    let (mut app, body) = fixture(false);
    app.update();
    let state = tick(app.world().resource::<CoreBridge>(), 12);
    assert_eq!(
        state["player"]["ammo"], 29,
        "chair early return cannot swallow hand actions"
    );
    let bridge = app.world().resource::<CoreBridge>();
    bridge
        .request("tick", json!({"input":{"heldItemId":0}}))
        .unwrap();
    let state = tick(bridge, 8);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.world_mut().resource_mut::<PlayerInput>().held_item_id = 0;
    app.world_mut().resource_mut::<PlayerInput>().fire_pressed = true;
    app.world_mut()
        .resource_mut::<crate::viewmodel::ViewmodelStatus>()
        .held = 0;
    app.update();
    let state = tick(app.world().resource::<CoreBridge>(), 35);
    assert!(
        state["events"]
            .as_array()
            .unwrap()
            .iter()
            .any(|e| e["kind"] == "melee")
    );
    assert_eq!(
        app.world().get::<Transform>(body).unwrap().translation,
        Vec3::ZERO
    );
    assert!(
        app.world()
            .get::<KinematicCharacterController>(body)
            .unwrap()
            .translation
            .is_none()
    );
}
