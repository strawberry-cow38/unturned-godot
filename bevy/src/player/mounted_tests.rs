//! Mounted controls use the production player reader and retain held equipment.
use super::*;
use crate::vehicle::{Occupancy, OccupiedSeat, turrets::Operator};

#[test]
fn boarding_resets_original_gaze_but_moving_between_seats_preserves_it() {
    let (mut app, _) = seated_tests::fixture(None);
    let tank = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    app.world_mut().resource_mut::<Occupancy>().seat = Some(OccupiedSeat {
        vehicle: tank,
        net_id: 71,
        seat: 0,
        body_pose: Transform::IDENTITY,
        eye_pose: Transform::from_rotation(Quat::from_rotation_y(75_f32.to_radians())),
        velocity: Vec3::ZERO,
        driver: true,
        aircraft: false,
    });
    app.world_mut().resource_mut::<PlayerInput>().yaw = 142.;
    app.world_mut().resource_mut::<PlayerInput>().pitch = 37.;
    app.world_mut().run_system_once(sync_seated_body).unwrap();
    assert_eq!(
        (
            app.world().resource::<PlayerInput>().yaw,
            app.world().resource::<PlayerInput>().pitch
        ),
        (0., 0.)
    );
    app.world_mut().resource_mut::<PlayerInput>().yaw = 32.;
    app.world_mut().resource_mut::<PlayerInput>().pitch = -17.;
    app.world_mut()
        .resource_mut::<Occupancy>()
        .seat
        .as_mut()
        .unwrap()
        .seat = 1;
    app.world_mut()
        .resource_mut::<Occupancy>()
        .seat
        .as_mut()
        .unwrap()
        .driver = false;
    app.world_mut().run_system_once(sync_seated_body).unwrap();
    assert_eq!(
        (
            app.world().resource::<PlayerInput>().yaw,
            app.world().resource::<PlayerInput>().pitch
        ),
        (32., -17.),
        "source TrySwitchSeat retains ride-look"
    );
    let jeep = crate::vehicle::tests::body(&mut app, "jeep", Vec3::ZERO);
    let mut occupied = app.world_mut().resource_mut::<Occupancy>();
    let seat = occupied.seat.as_mut().unwrap();
    seat.vehicle = jeep;
    seat.net_id = 72;
    seat.seat = 0;
    seat.driver = true;
    app.world_mut().run_system_once(sync_seated_body).unwrap();
    assert_eq!(
        (
            app.world().resource::<PlayerInput>().yaw,
            app.world().resource::<PlayerInput>().pitch
        ),
        (0., -8.75)
    );
}
use bevy::ecs::system::RunSystemOnce;

#[derive(Resource, Default)]
struct DelayedSeat(Option<OccupiedSeat>);
#[derive(Resource, Default)]
struct LookAtInputFrame {
    yaw: f32,
    pitch: f32,
    cached_seat: Option<(u32, u8)>,
}
fn read_look_result(
    input: Res<PlayerInput>,
    occupancy: Res<Occupancy>,
    mut result: ResMut<LookAtInputFrame>,
) {
    result.yaw = input.yaw;
    result.pitch = input.pitch;
    result.cached_seat = occupancy.seat.map(|s| (s.net_id, s.seat));
}
fn delayed_seat_adoption(next: Res<DelayedSeat>, mut occupancy: ResMut<Occupancy>) {
    occupancy.seat = next.0;
}

#[test]
fn accepted_seat_on_receiving_frame_controls_actual_look_before_physical_adoption() {
    let (mut app, _) = seated_tests::fixture(None);
    // This fixture needs the actual input schedule, not its ordinary simulated
    // fixed-step harness. Physical pose adoption still follows PlayerUpdate.
    app.add_schedule(Schedule::new(Update));
    app.init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<AccumulatedMouseMotion>()
        .init_resource::<Settings>()
        .init_resource::<crate::recoil::Recoil>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<crate::world::WorldConfig>()
        .init_resource::<Operator>()
        .init_resource::<crate::ui::tank_optics::TankOptics>()
        .init_resource::<DelayedSeat>()
        .init_resource::<LookAtInputFrame>()
        .configure_sets(
            Update,
            (PlayerUpdate, crate::vehicle::VehicleUpdate).chain(),
        )
        .add_systems(
            Update,
            (read_input, read_look_result).chain().in_set(PlayerUpdate),
        )
        .add_systems(
            Update,
            delayed_seat_adoption.in_set(crate::vehicle::VehicleUpdate),
        );
    crate::ui::tank_optics::install_input_system(&mut app);
    app.world_mut().resource_mut::<Settings>().mouse_sensitivity = 1.;
    {
        let mut vm = app
            .world_mut()
            .resource_mut::<crate::viewmodel::ViewmodelStatus>();
        vm.aim_alpha = 1.;
        vm.scope_zoom = 4.;
    }
    let tank = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    let type_id = app
        .world()
        .get::<crate::vehicle::VehicleBody>(tank)
        .unwrap()
        .type_id;
    let initial = OccupiedSeat {
        vehicle: tank,
        net_id: 1,
        seat: 0,
        body_pose: default(),
        eye_pose: default(),
        velocity: Vec3::ZERO,
        driver: true,
        aircraft: false,
    };
    app.world_mut().resource_mut::<Occupancy>().seat = Some(initial);
    let owner = app.world().resource::<GameSnapshot>().0["playerId"].clone();
    let mut receive =
        |app: &mut App, seat: Option<u8>, yaw: f32, pitch: f32, mouse_x: f32, aim: bool| {
            let mut state = app.world_mut().resource_mut::<GameSnapshot>();
            state.0["player"]["vehicleNetId"] = json!(if seat.is_some() { 1 } else { 0 });
            state.0["player"]["vehicleSeat"] = json!(seat.map_or(-1, i32::from));
            state.0["vehicles"] = json!([{"netId":1,"typeId":type_id,
            "driverPlayerId":if seat == Some(0) {owner.clone()} else {json!(0)},
            "passengers":[if seat == Some(1) {owner.clone()} else {json!(0)},
                if seat == Some(2) {owner.clone()} else {json!(0)}]}]);
            drop(state);
            app.world_mut().resource_mut::<DelayedSeat>().0 = seat.map(|seat| OccupiedSeat {
                seat,
                driver: seat == 0,
                ..initial
            });
            {
                let mut input = app.world_mut().resource_mut::<PlayerInput>();
                input.yaw = yaw;
                input.pitch = pitch;
            }
            app.world_mut()
                .resource_mut::<AccumulatedMouseMotion>()
                .delta = Vec2::new(mouse_x, 0.);
            if aim {
                app.world_mut()
                    .resource_mut::<ButtonInput<MouseButton>>()
                    .press(MouseButton::Right);
            }
            app.update();
            app.world_mut()
                .resource_mut::<ButtonInput<MouseButton>>()
                .reset_all();
        };
    receive(&mut app, Some(1), 0., 45., -120., true);
    let result = app.world().resource::<LookAtInputFrame>();
    assert_eq!(result.cached_seat, Some((1, 0)));
    assert!(
        (result.yaw - 10.).abs() < 0.001,
        "new gunner cycles to 12x before actual look"
    );
    assert_eq!(result.pitch, 45., "gunner retains source unlimited look");

    receive(&mut app, Some(0), 170., 45., 0., true);
    let result = app.world().resource::<LookAtInputFrame>();
    assert_eq!(result.cached_seat, Some((1, 1)));
    assert_eq!(
        (result.yaw, result.pitch),
        (160., 30.),
        "new driver uses driver bounds immediately"
    );

    receive(&mut app, Some(2), 170., 45., 0., false);
    let result = app.world().resource::<LookAtInputFrame>();
    assert_eq!(result.cached_seat, Some((1, 0)));
    assert_eq!(
        (result.yaw, result.pitch),
        (90., 30.),
        "new passenger uses passenger bounds"
    );

    let held_sensitivity = app
        .world()
        .resource::<crate::viewmodel::ViewmodelStatus>()
        .look_sensitivity();
    receive(&mut app, None, 0., 45., -80., true);
    let result = app.world().resource::<LookAtInputFrame>();
    assert_eq!(result.cached_seat, Some((1, 2)));
    assert!(
        (result.yaw - 80. * held_sensitivity).abs() < 0.001,
        "accepted exit restores held optic sensitivity"
    );
    assert_eq!(
        result.pitch, 45.,
        "accepted exit removes seated pitch clamp"
    );

    receive(&mut app, Some(1), 0., 45., -120., false);
    let result = app.world().resource::<LookAtInputFrame>();
    assert_eq!(result.cached_seat, None);
    assert!(
        (result.yaw - 10.).abs() < 0.001,
        "entry uses retained 12x without the held 4x sensitivity"
    );
}

#[test]
fn source_mount_hotkeys_keep_rifle_and_seated_look_uses_gunsight_sensitivity() {
    let (mut app, _) = seated_tests::fixture(None);
    app.init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<AccumulatedMouseMotion>()
        .init_resource::<Settings>()
        .init_resource::<crate::recoil::Recoil>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<crate::world::WorldConfig>()
        .init_resource::<Operator>()
        .init_resource::<crate::ui::tank_optics::TankOptics>();
    let tank = crate::vehicle::data::named("tank").unwrap();
    let body = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    app.world_mut()
        .get_mut::<crate::vehicle::VehicleBody>(body)
        .unwrap()
        .net_id = 7;
    let mut state = app.world().resource::<GameSnapshot>().0.clone();
    let owner = state["playerId"].clone();
    state["vehicles"] =
        json!([{"netId":7,"typeId":tank.type_id,"driverPlayerId":0,"passengers":[owner]}]);
    state["player"]["vehicleNetId"] = json!(7);
    state["player"]["vehicleSeat"] = json!(1);
    state["vehicleMounts"] = json!([{"vehicleNetId":7,"headOut":false,"mounts":[
        {"index":0,"yaw":0.,"pitch":0.,"ammo":60,"cooldownSeconds":0.,"crewAlive":true},
        {"index":1,"yaw":0.,"pitch":0.,"ammo":400,"cooldownSeconds":0.,"crewAlive":true}]}]);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    app.world_mut().resource_mut::<Occupancy>().seat = Some(OccupiedSeat {
        vehicle: body,
        net_id: 7,
        seat: 1,
        body_pose: Transform::IDENTITY,
        eye_pose: Transform::IDENTITY,
        velocity: Vec3::ZERO,
        driver: false,
        aircraft: false,
    });
    {
        let mut input = app.world_mut().resource_mut::<PlayerInput>();
        input.in_seat = true;
        input.yaw = 179.;
        input.pitch = 45.;
    }
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::Digit2);
    app.world_mut()
        .resource_mut::<AccumulatedMouseMotion>()
        .delta = Vec2::new(-80., 0.);
    crate::ui::tank_optics::run_input_system(app.world_mut());
    assert!(
        app.world()
            .resource::<crate::ui::tank_optics::TankOptics>()
            .gunsight_active()
    );
    app.world_mut().run_system_once(read_input).unwrap();
    assert_eq!(
        app.world()
            .resource::<Operator>()
            .active()
            .unwrap()
            .definition
            .gun_id,
        "hmg"
    );
    let input = app.world().resource::<PlayerInput>();
    let sensitivity = app.world().resource::<Settings>().mouse_sensitivity;
    let expected = (179. + 80. * sensitivity / 8. + 180.).rem_euclid(360.) - 180.;
    assert!((input.yaw - expected).abs() < 0.001);
    assert_eq!(
        input.pitch, 45.,
        "turret gunner is exempt from ordinary passenger head limits"
    );
    assert_eq!(
        input.held_item_id, 4,
        "mount hotkey must not holster or replace the accepted rifle"
    );
    // Ordinary passenger and crosshair-mount driver keep source limits.
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.world_mut()
        .resource_mut::<AccumulatedMouseMotion>()
        .delta = Vec2::ZERO;
    for (driver, limit) in [(false, 90.), (true, 160.)] {
        let mut state = app.world().resource::<GameSnapshot>().0.clone();
        state["vehicles"][0]["driverPlayerId"] = if driver {
            state["playerId"].clone()
        } else {
            json!(0)
        };
        state["vehicles"][0]["passengers"] = json!([0, state["playerId"].clone()]);
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        let mut seat = app.world_mut().resource_mut::<Occupancy>();
        seat.seat.as_mut().unwrap().seat = if driver { 0 } else { 2 };
        seat.seat.as_mut().unwrap().driver = driver;
        let mut input = app.world_mut().resource_mut::<PlayerInput>();
        input.yaw = 175.;
        input.pitch = -70.;
        app.world_mut().run_system_once(read_input).unwrap();
        let input = app.world().resource::<PlayerInput>();
        assert_eq!((input.yaw, input.pitch), (limit, -30.));
    }
}
