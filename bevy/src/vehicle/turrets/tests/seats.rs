use super::*;
use crate::{
    player::GameSnapshot,
    vehicle::{Occupancy, adopt_occupancy},
};
use serde_json::json;

mod visual;
mod ordinary;

fn near(actual: Vec3, expected: Vec3) {
    assert!(
        actual.distance(expected) < 0.0001,
        "{actual:?} != {expected:?}"
    );
}

#[test]
fn source_tank_seat_uses_yaw_frame_and_exact_anchors_without_cannon_pitch() {
    let mut app = app();
    let entity = build(&mut app, "tank", Vec3::ZERO);
    let definition = crate::vehicle::data::named("tank").unwrap();
    for yaw in [-90_f32, 0., 73., 180.] {
        for pitch in [-10., 25.] {
            let mut mounts = app.world_mut().get_mut::<Mounts>(entity).unwrap();
            mounts.angles[0] = Vec2::new(yaw, pitch);
            mounts.gunner_rise = 0.5;
            let turn = Quat::from_rotation_y(yaw.to_radians());
            let pivot = Vec3::new(0., 0., 0.85);
            let body = mounts.seat_body(definition, 1).unwrap();
            near(body.translation, pivot + turn * Vec3::new(0.44, 2.35, 0.60));
            assert!(body.rotation.abs_diff_eq(turn, 0.00001));
            mounts.head_out = false;
            near(
                mounts.seat_eye(definition, 1).unwrap(),
                pivot + turn * Vec3::new(0., 4.3, -0.8),
            );
            mounts.head_out = true;
            near(
                mounts.seat_eye(definition, 1).unwrap(),
                pivot + turn * Vec3::new(0.44, 4.2, 0.60),
            );
            near(
                mounts.seat_eye(definition, 0).unwrap(),
                Vec3::new(0., 1.98, -3.92),
            );
            assert!(mounts.seat_body(definition, 0).is_none());
            assert!(mounts.seat_eye(definition, 2).is_none());
        }
    }
}

#[test]
fn production_occupancy_keeps_optics_hull_basis_and_body_turret_basis() {
    let mut app = app();
    let tank = build(&mut app, "tank", Vec3::new(12., 4., -7.));
    let hull = Transform::from_xyz(12., 4., -7.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        0.7,
        -0.1,
        0.2,
    ));
    *app.world_mut().get_mut::<Transform>(tank).unwrap() = hull;
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .passengers = vec![7];
    app.world_mut().get_mut::<Velocity>(tank).unwrap().linear = Vec3::new(3., -1., 2.);
    let row = |rise: f32, head_out: bool| {
        json!({"playerId":7, "vehicleMounts":[{
            "vehicleNetId":1,"headOut":head_out,"gunnerRise":rise,
            "mounts":[{"index":0,"yaw":90.,"pitch":20.,"ammo":100,"cooldownSeconds":0.,"crewAlive":true},
                      {"index":1,"yaw":-20.,"pitch":0.,"ammo":100,"cooldownSeconds":0.,"crewAlive":true}]
        }]})
    };
    app.insert_resource(GameSnapshot(row(0., false)))
        .init_resource::<Occupancy>();
    app.add_systems(Update, input::adopt.before(animate));
    app.add_systems(Update, adopt_occupancy.after(animate));
    for (rise, head_out) in [(0., false), (0.5, true), (1., true)] {
        app.world_mut().resource_mut::<GameSnapshot>().0 = row(rise, head_out);
        ticks(&mut app, 2);
        let seat = app.world().resource::<Occupancy>().seat.unwrap();
        let yaw = Quat::from_rotation_y(std::f32::consts::FRAC_PI_2);
        near(
            seat.body_pose.translation,
            hull.transform_point(
                Vec3::new(0., 0., 0.85) + yaw * Vec3::new(0.44, 2. + 0.7 * rise, 0.60),
            ),
        );
        assert!(
            seat.body_pose
                .rotation
                .abs_diff_eq(hull.rotation * yaw, 0.00001)
        );
        let optic = if head_out {
            Vec3::new(0.44, 4.2, 0.60)
        } else {
            Vec3::new(0., 4.3, -0.8)
        };
        near(
            seat.eye_pose.translation,
            hull.transform_point(Vec3::new(0., 0., 0.85) + yaw * optic),
        );
        assert!(
            seat.eye_pose.rotation.abs_diff_eq(hull.rotation, 0.00001),
            "view direction must not apply turret yaw twice"
        );
        near(seat.velocity, Vec3::new(3., -1., 2.));
        assert_eq!(
            app.world().get::<Mounts>(tank).unwrap().gunner_rise,
            rise,
            "accepted phase is not eased a second time"
        );
    }
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .passengers
        .clear();
    app.update();
    assert!(app.world().resource::<Occupancy>().seat.is_none());
}

#[test]
fn source_fallback_rise_runs_three_per_second_and_pauses_only_singleplayer() {
    let mut app = app();
    let tank = build(&mut app, "tank", Vec3::ZERO);
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .passengers = vec![7];
    app.world_mut().get_mut::<Mounts>(tank).unwrap().head_out = true;
    app.update(); // Time's first update has zero delta.
    ticks(&mut app, 5);
    assert!((app.world().get::<Mounts>(tank).unwrap().gunner_rise - 0.30).abs() < 0.0001);
    app.insert_resource(crate::ui::UiState {
        screen: crate::ui::Screen::Paused,
        ..default()
    });
    ticks(&mut app, 20);
    assert!((app.world().get::<Mounts>(tank).unwrap().gunner_rise - 0.30).abs() < 0.0001);
    app.world_mut()
        .resource_mut::<crate::ui::UiState>()
        .networked = true;
    ticks(&mut app, 20);
    assert_eq!(app.world().get::<Mounts>(tank).unwrap().gunner_rise, 1.);
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .passengers
        .clear();
    ticks(&mut app, 5);
    let mounts = app.world().get::<Mounts>(tank).unwrap();
    assert!(!mounts.head_out);
    assert!((mounts.gunner_rise - 0.70).abs() < 0.0001);
}

#[test]
fn ordinary_vehicle_body_seats_and_tank_driver_keep_original_offsets() {
    for key in ["jeep", "tank"] {
        let mut app = app();
        let vehicle = build(&mut app, key, Vec3::new(-9., 2., 11.));
        let definition = crate::vehicle::data::named(key).unwrap();
        let hull = pose(&app, vehicle).with_rotation(Quat::from_rotation_y(-0.8));
        *app.world_mut().get_mut::<Transform>(vehicle).unwrap() = hull;
        app.insert_resource(GameSnapshot(json!({"playerId":7})))
            .init_resource::<Occupancy>();
        for seat in 0..if key == "tank" {
            1
        } else {
            definition.seats.len()
        } {
            {
                let mut body = app.world_mut().get_mut::<VehicleBody>(vehicle).unwrap();
                body.driver_player_id = if seat == 0 { 7 } else { 0 };
                body.passengers = vec![0; definition.seats.len() - 1];
                if seat > 0 {
                    body.passengers[seat - 1] = 7;
                }
            }
            app.world_mut().run_system_once(adopt_occupancy).unwrap();
            let actual = app.world().resource::<Occupancy>().seat.unwrap();
            near(
                actual.body_pose.translation,
                hull.transform_point(Vec3::from_array(definition.body_seats[seat])),
            );
            assert!(
                actual
                    .body_pose
                    .rotation
                    .abs_diff_eq(hull.rotation, 0.00001)
            );
            if seat == 0 {
                let eye = if key == "tank" {
                    Vec3::new(0., 1.98, -3.92)
                } else if definition.spec.driver_eye == [0.; 3] {
                    Vec3::new(-0.4, 1.85, 0.4)
                } else {
                    Vec3::from_array(definition.spec.driver_eye)
                };
                near(actual.eye_pose.translation, hull.transform_point(eye));
            }
        }
    }
}

#[test]
fn actual_authority_rise_and_body_anchor_are_adopted_in_the_same_native_frame() {
    use crate::{bridge::CoreBridge, player::vector};
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR")))
        .expect("Build the current bridge with bevy/check.sh before native authority tests");
    let config = WorldConfig::default();
    bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root.canonicalize().unwrap(),"spawn":[0,0,0]}),
        )
        .unwrap();
    let type_id = crate::vehicle::data::named("tank").unwrap().type_id;
    let reply=bridge.request("worldVehicles",json!({"vehicles":[{"typeId":type_id,"key":"native tank anchor","position":[0,0,0],"seatCount":2,"health":10000,"fuel":100,"speedMax":40}]})).unwrap();
    let id = reply["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
    bridge
        .request(
            "action",
            json!({"name":"enterVehicle","args":{"netId":id,"seat":1}}),
        )
        .unwrap();
    for _ in 0..12 {
        bridge.request("tick", json!({})).unwrap();
    }
    bridge
        .request(
            "worldVehicleState",
            json!({"vehicles":[{"netId":id,"position":[0,2,0],"eulerDegrees":[7,25,-5]}]}),
        )
        .unwrap();
    bridge.request("action",json!({"name":"mountInput","args":{"netId":id,"slot":0,"aimMode":"point","target":[-80,20,-50],"headOut":true,"trigger":false}})).unwrap();
    let mut app = app();
    let tank = build(&mut app, "tank", Vec3::ZERO);
    app.init_resource::<Occupancy>()
        .insert_resource(GameSnapshot(json!({})));
    app.add_systems(Update, input::adopt.before(animate));
    app.add_systems(Update, adopt_occupancy.after(animate));
    let mut partial = false;
    for _ in 0..24 {
        let state = bridge.request("tick", json!({})).unwrap()["state"].clone();
        let rise = state["vehicleMounts"][0]["gunnerRise"].as_f64().expect(
            "Current protocol65+ bridge must expose accepted gunnerRise; rebuild via bevy/check.sh",
        ) as f32;
        partial |= rise > 0. && rise < 1.;
        let row: crate::vehicle::Replica =
            serde_json::from_value(state["vehicles"][0].clone()).unwrap();
        *app.world_mut().get_mut::<Transform>(tank).unwrap() = crate::vehicle::replica_pose(&row);
        {
            let mut body = app.world_mut().get_mut::<VehicleBody>(tank).unwrap();
            body.net_id = id;
            body.driver_player_id = row.driver_player_id;
            body.passengers = row.passengers.unwrap_or_default();
        }
        // The owner block keeps the native body's last published pose. Combat
        // authority is the accepted player replica, just as a UDP observer sees.
        let rider = state["players"]
            .as_array()
            .unwrap()
            .iter()
            .find(|p| p["playerId"] == state["playerId"])
            .unwrap();
        let accepted = vector(&rider["position"]).unwrap();
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        app.update();
        let native = app.world().resource::<Occupancy>().seat.unwrap();
        assert!((app.world().get::<Mounts>(tank).unwrap().gunner_rise - rise).abs() < 1e-6);
        assert!(
            native.body_pose.translation.distance(accepted) < 0.035,
            "native source body and accepted wire body stay in the same snapshot: {:?} vs {:?}",
            native.body_pose.translation,
            accepted
        );
    }
    assert!(
        partial,
        "exercise authoritative partial rise, not only endpoints"
    );
}
