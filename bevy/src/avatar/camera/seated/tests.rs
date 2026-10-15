use super::*;
use crate::{
    avatar::{AvatarUpdate, Perspective, camera::VehicleCameras},
    player::{PlayerInput, simulation_paused},
    ui::UiState,
    vehicle::{Occupancy, data},
};

fn near(actual: Vec3, expected: Vec3) {
    assert!(
        actual.distance(expected) < 0.0002,
        "{actual:?} != {expected:?}"
    );
}
fn seat(vehicle: Entity, hull: Transform, index: usize) -> OccupiedSeat {
    let definition = data::named("jeep").unwrap();
    let body = Vec3::from_array(definition.body_seats[index]);
    let fallback = if index == 0 {
        Vec3::from_array(definition.spec.driver_eye)
    } else {
        Vec3::from_array(definition.seats[index]) + Vec3::Y * 1.05
    };
    OccupiedSeat {
        vehicle,
        net_id: 1,
        seat: index as u8,
        body_pose: hull.mul_transform(Transform::from_translation(body)),
        eye_pose: hull.mul_transform(Transform::from_translation(fallback)),
        velocity: Vec3::ZERO,
        driver: index == 0,
        aircraft: false,
    }
}
#[test]
fn source_skull_eye_distinguishes_missing_and_unposed_bones_and_keeps_vehicle_axis_offsets() {
    let body = Vec3::new(-0.4, 0.7, 0.1);
    let fallback = Vec3::new(-0.4, 2.2, 0.2);
    assert_eq!(skull_eye(body, fallback, None), fallback);
    near(
        skull_eye(body, fallback, Some(Vec3::ZERO)),
        fallback + Vec3::Y * 0.15,
    );
    near(
        skull_eye(body, fallback, Some(Vec3::Y * 0.099)),
        fallback + Vec3::Y * 0.15,
    );
    let head = Vec3::new(0.03, 1.1, -0.07);
    near(
        skull_eye(body, fallback, Some(head)),
        body + head + Vec3::new(0., 0.31, -0.10),
    );
}
#[test]
fn source_driver_peek_has_deadband_asymmetry_pause_and_same_vehicle_seat_decay() {
    let mut state = State::default();
    let driver = seat(Entity::PLACEHOLDER, Transform::IDENTITY, 0);
    let passenger = OccupiedSeat {
        driver: false,
        seat: 1,
        ..driver
    };
    assert_eq!(state.step(0, Some(driver), true, 40., 1., false), 0.);
    assert!((state.step(0, Some(driver), true, 160., 0.02, false) + 0.04).abs() < 1e-6);
    assert_eq!(state.step(0, Some(driver), true, 160., 1., false), -0.5);
    assert_eq!(
        state.step(0, Some(driver), true, -160., 1., true),
        -0.5,
        "SP pause"
    );
    assert!((state.step(0, Some(passenger), true, 160., 0.02, false) + 0.46).abs() < 1e-6);
    assert_eq!(state.step(0, Some(driver), true, -160., 1., false), 1. / 3.);
    assert_eq!(
        state.step(0, Some(driver), false, -160., 1., false),
        0.,
        "TP eases home"
    );
    state.step(0, Some(driver), true, 160., 1., false);
    assert_eq!(
        state.step(1, Some(driver), true, 0., 0., false),
        0.,
        "new session resets"
    );
    state.step(1, Some(driver), true, 160., 1., false);
    assert_eq!(state.step(1, None, true, 0., 0., false), 0.);
    assert_eq!(
        state.step(1, Some(driver), true, 0., 0., false),
        0.,
        "entry resets"
    );
}
#[derive(Resource, Default)]
struct ResultEye(Option<Transform>);
fn probe(
    mut cameras: VehicleCameras,
    seat: Res<Occupancy>,
    view: Res<Perspective>,
    input: Res<PlayerInput>,
    ui: Res<UiState>,
    time: Res<Time>,
    mut result: ResMut<ResultEye>,
) {
    result.0 = cameras.seated_eye(
        seat.seat,
        &view,
        input.yaw(),
        time.delta_secs(),
        simulation_paused(&ui),
    );
}
fn posed_app() -> (App, Entity, Transform) {
    let mut app = crate::avatar::tests::avatar_app();
    let hull = Transform::from_xyz(17., 4., -21.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        0.71,
        -0.22,
        0.18,
    ));
    let vehicle = crate::vehicle::tests::body(&mut app, "jeep", hull.translation);
    *app.world_mut().get_mut::<Transform>(vehicle).unwrap() = hull;
    app.world_mut().resource_mut::<Occupancy>().seat = Some(seat(vehicle, hull, 0));
    app.init_resource::<ResultEye>()
        .add_systems(Update, probe.before(AvatarUpdate));
    for _ in 0..3 {
        app.update();
    }
    (app, vehicle, hull)
}
fn head(app: &mut App) -> (Entity, Entity, Vec3) {
    let (root, rig, global) = app
        .world_mut()
        .query_filtered::<(Entity, &RigPlayback, &GlobalTransform), With<LocalAvatar>>()
        .single(app.world())
        .unwrap();
    let skull = rig.bones["Skull"];
    let local = global.affine().inverse().transform_point3(
        app.world()
            .get::<GlobalTransform>(skull)
            .unwrap()
            .translation(),
    );
    (root, skull, local)
}
#[test]
fn actual_drive_and_sit_clips_supply_eye_through_tilted_current_hull_without_rotating_offset() {
    let (mut app, vehicle, mut hull) = posed_app();
    for (index, clip, pitch) in [(0, "Idle_Drive", 0.), (1, "Idle_Sit", 60.)] {
        app.world_mut().resource_mut::<Occupancy>().seat = Some(seat(vehicle, hull, index));
        app.insert_resource(PlayerInput::with_view(0., pitch));
        for _ in 0..3 {
            app.update();
        }
        let (root, _, skull) = head(&mut app);
        assert_eq!(app.world().get::<RigPlayback>(root).unwrap().clip, clip);
        assert!(skull.length_squared() > 0.01);
        let body = Vec3::from_array(data::named("jeep").unwrap().body_seats[index]);
        let expected = hull.transform_point(body + skull + Vec3::new(0., 0.31, -0.10));
        app.update(); // camera consumes the completed pose sampled above
        let eye = app.world().resource::<ResultEye>().0.unwrap();
        near(eye.translation, expected);
        assert!(eye.rotation.abs_diff_eq(hull.rotation, 1e-6));
        // Move/rotate the hull without propagating either rig global. The eye
        // follows the current seat, not the previous world-space skull position.
        let (_, _, skull) = head(&mut app);
        hull = Transform::from_xyz(-11., 8., 24.).with_rotation(Quat::from_euler(
            EulerRot::YXZ,
            -0.6,
            0.13,
            -0.3,
        ));
        *app.world_mut().get_mut::<Transform>(vehicle).unwrap() = hull;
        app.world_mut().resource_mut::<Occupancy>().seat = Some(seat(vehicle, hull, index));
        app.update();
        near(
            app.world().resource::<ResultEye>().0.unwrap().translation,
            hull.transform_point(body + skull + Vec3::new(0., 0.31, -0.10)),
        );
    }
}
#[test]
fn missing_skull_and_session_replacement_use_fallback_without_reusing_old_rig() {
    let (mut app, vehicle, hull) = posed_app();
    let (root, _, _) = head(&mut app);
    let old = app
        .world_mut()
        .get_mut::<RigPlayback>(root)
        .unwrap()
        .bones
        .remove("Skull")
        .unwrap();
    app.update();
    near(
        app.world().resource::<ResultEye>().0.unwrap().translation,
        seat(vehicle, hull, 0).eye_pose.translation,
    );
    app.world_mut()
        .get_mut::<RigPlayback>(root)
        .unwrap()
        .bones
        .insert("Skull".into(), old);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    near(
        app.world().resource::<ResultEye>().0.unwrap().translation,
        seat(vehicle, hull, 0).eye_pose.translation,
    );
    assert!(
        app.world().get_entity(root).is_err(),
        "old session rig removed after camera used its safe fallback"
    );
}
