use super::*;
#[test]
fn original_mode_gates_exclude_passengers_aircraft_dead_third_person_and_outside_harness() {
    assert_eq!(
        selected_mode(true, true, false, true, 0, false),
        Mode::Periscope
    );
    assert_eq!(
        selected_mode(true, true, false, true, 1, false),
        Mode::Gunsight
    );
    assert_eq!(selected_mode(true, true, false, true, 1, true), Mode::None);
    for (alive, fp, hidden, tank, seat) in [
        (false, true, false, true, 1),
        (true, false, false, true, 1),
        (true, true, true, true, 1),
        (true, true, false, false, 1),
        (true, true, false, true, 2),
    ] {
        assert_eq!(
            selected_mode(alive, fp, hidden, tank, seat, false),
            Mode::None
        );
    }
}

fn app() -> (App, Entity, Entity) {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin {
            file_path: WorldConfig::default()
                .content_root
                .canonicalize()
                .unwrap()
                .to_string_lossy()
                .into_owned(),
            ..default()
        },
        bevy::image::ImagePlugin::default(),
    ))
    .init_asset::<OpticsMaterial>()
    .init_resource::<TankOptics>()
    .init_resource::<CameraState>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Perspective>()
    .init_resource::<WorldConfig>()
    .init_resource::<Settings>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .insert_resource(UiState {
        screen: super::super::Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(
        serde_json::json!({"connected":true,"player":{"alive":true}}),
    ))
    .add_systems(Startup, spawn)
    .configure_sets(
        Update,
        (
            PlayerUpdate,
            crate::vehicle::VehicleUpdate,
            PlayerCameraUpdate,
        )
            .chain(),
    )
    .add_systems(Update, reset_camera.in_set(PlayerCameraUpdate));
    // Use the production registration: optics input precedes PlayerUpdate,
    // whereas final image/FOV adoption follows the vehicle and camera stages.
    install_update_systems(&mut app);
    let vehicle = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    app.insert_resource(Occupancy {
        seat: Some(crate::vehicle::OccupiedSeat {
            vehicle,
            net_id: 1,
            seat: 1,
            body_pose: default(),
            eye_pose: default(),
            velocity: Vec3::ZERO,
            driver: false,
            aircraft: false,
        }),
    });
    let camera = app
        .world_mut()
        .spawn((
            PlayerCamera,
            Projection::Perspective(PerspectiveProjection {
                fov: 72_f32.to_radians(),
                ..default()
            }),
        ))
        .id();
    app.world_mut().resource_mut::<Settings>().field_of_view = 72.;
    app.world_mut().spawn((
        Window {
            resolution: (1280, 720).into(),
            ..default()
        },
        PrimaryWindow,
    ));
    (app, vehicle, camera)
}
// The real PlayerCameraUpdate writes the base setting every frame. Keeping this
// behavior in the fixture catches accidental repeated FOV division immediately.
fn reset_camera(settings: Res<Settings>, mut camera: Query<&mut Projection, With<PlayerCamera>>) {
    for mut p in &mut camera {
        if let Projection::Perspective(p) = &mut *p {
            p.fov = settings.field_of_view.to_radians();
        }
    }
}
fn fov(app: &App, camera: Entity) -> f32 {
    match app.world().get::<Projection>(camera).unwrap() {
        Projection::Perspective(p) => p.fov.to_degrees(),
        _ => panic!("perspective"),
    }
}
fn aim(app: &mut App) {
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Right);
    app.update();
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .reset_all();
}
fn overlay(app: &mut App) -> (Visibility, Vec4) {
    let (v, h) = app
        .world_mut()
        .query_filtered::<(&Visibility, &MaterialNode<OpticsMaterial>), With<Overlay>>()
        .single(app.world())
        .unwrap();
    (
        *v,
        app.world()
            .resource::<Assets<OpticsMaterial>>()
            .get(&h.0)
            .unwrap()
            .settings,
    )
}
#[test]
fn accepted_tank_seats_cycle_source_fov_without_accumulation_and_restore_on_exit() {
    let (mut app, _, camera) = app();
    for _ in 0..60 {
        app.update();
    }
    assert!((fov(&app, camera) - 9.).abs() < 1e-5);
    assert_eq!(
        app.world().resource::<TankOptics>().look_sensitivity(),
        1. / 8.
    );
    aim(&mut app);
    assert!((fov(&app, camera) - 6.).abs() < 1e-5);
    aim(&mut app);
    assert!((fov(&app, camera) - 18.).abs() < 1e-5);
    aim(&mut app);
    assert!((fov(&app, camera) - 9.).abs() < 1e-5);
    app.world_mut()
        .resource_mut::<Occupancy>()
        .seat
        .as_mut()
        .unwrap()
        .seat = 0;
    app.update();
    assert!((fov(&app, camera) - 72.).abs() < 1e-5);
    assert_eq!(app.world().resource::<TankOptics>().mode, Mode::Periscope);
    assert_eq!(app.world().resource::<TankOptics>().look_sensitivity(), 1.);
    assert_eq!(overlay(&mut app).0, Visibility::Inherited);
    app.world_mut().resource_mut::<Occupancy>().seat = None;
    app.update();
    assert!((fov(&app, camera) - 72.).abs() < 1e-5);
    assert_eq!(overlay(&mut app).0, Visibility::Hidden);
}
#[test]
fn modal_input_death_perspective_session_and_missing_body_follow_actual_system_gates() {
    let (mut app, vehicle, camera) = app();
    app.update();
    aim(&mut app);
    assert_eq!(app.world().resource::<TankOptics>().zoom(), 12.);
    app.world_mut().resource_mut::<UiState>().screen = super::super::Screen::Paused;
    aim(&mut app);
    assert_eq!(app.world().resource::<TankOptics>().zoom(), 12.);
    // Source overlay remains underneath pause while its input is blocked.
    assert_eq!(overlay(&mut app).0, Visibility::Inherited);
    app.world_mut().resource_mut::<UiState>().screen = super::super::Screen::Playing;
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["alive"] = false.into();
    app.update();
    assert_eq!(overlay(&mut app).0, Visibility::Hidden);
    assert!((fov(&app, camera) - 72.).abs() < 1e-5);
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["alive"] = true.into();
    app.update();
    assert_eq!(app.world().resource::<TankOptics>().zoom(), 12.);
    app.world_mut().resource_mut::<Perspective>().first_person = false;
    app.update();
    assert_eq!(app.world().resource::<TankOptics>().mode, Mode::None);
    app.world_mut().resource_mut::<Perspective>().first_person = true;
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert_eq!(app.world().resource::<TankOptics>().zoom(), 8.);
    app.world_mut().despawn(vehicle);
    app.update();
    assert_eq!(app.world().resource::<TankOptics>().mode, Mode::None);
    assert_eq!(overlay(&mut app).0, Visibility::Hidden);
}
#[test]
fn viewport_resize_and_camera_replacement_keep_source_aspect_and_capture_fresh_baseline() {
    let (mut app, _, camera) = app();
    app.update();
    assert!((overlay(&mut app).1.x - 16. / 9.).abs() < 1e-5);
    app.world_mut()
        .query::<&mut Window>()
        .single_mut(app.world_mut())
        .unwrap()
        .resolution
        .set(900., 900.);
    app.update();
    assert_eq!(overlay(&mut app).1.x, 1.);
    app.world_mut().despawn(camera);
    app.update();
    assert_eq!(overlay(&mut app).0, Visibility::Hidden);
    app.world_mut().resource_mut::<Settings>().field_of_view = 90.;
    let replacement = app
        .world_mut()
        .spawn((PlayerCamera, Projection::Perspective(default())))
        .id();
    app.update();
    assert!((fov(&app, replacement) - 11.25).abs() < 1e-5);
    app.world_mut().resource_mut::<Occupancy>().seat = None;
    app.update();
    assert!((fov(&app, replacement) - 90.).abs() < 1e-5);
}

mod visual;

#[test]
fn newly_accepted_hatch_fact_gates_same_frame_zoom_before_vehicle_component_adoption() {
    let (mut app, _, camera) = app();
    app.update();
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicleMounts"] =
        serde_json::json!([{"vehicleNetId":1,"headOut":true}]);
    aim(&mut app);
    assert_eq!(app.world().resource::<TankOptics>().mode, Mode::None);
    assert!((fov(&app, camera) - 72.).abs() < 1e-5);
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicleMounts"][0]["headOut"] = false.into();
    app.update();
    assert_eq!(
        app.world().resource::<TankOptics>().zoom(),
        8.,
        "masked-off Aim did not advance zoom"
    );
    assert!((fov(&app, camera) - 9.).abs() < 1e-5);
}

#[derive(Resource, Default)]
struct ReceivingFrame {
    mode: Mode,
    sensitivity: f32,
    index: usize,
    native_seat: Option<(u32, u8)>,
}
#[derive(Resource, Default)]
struct NextNativeSeat(Option<crate::vehicle::OccupiedSeat>);
fn observe_input(
    optics: Res<TankOptics>,
    occupancy: Res<Occupancy>,
    mut observed: ResMut<ReceivingFrame>,
) {
    *observed = ReceivingFrame {
        mode: optics.source_mode,
        sensitivity: optics.look_sensitivity(),
        index: optics.index,
        native_seat: occupancy.seat.map(|seat| (seat.net_id, seat.seat)),
    };
}
fn adopt_native_seat(next: Res<NextNativeSeat>, mut occupancy: ResMut<Occupancy>) {
    occupancy.seat = next.0;
}
#[test]
fn receiving_frame_seat_changes_use_accepted_input_gate_before_native_adoption() {
    let (mut app, tank, camera) = app();
    let other_tank = crate::vehicle::tests::body(&mut app, "tank", Vec3::ZERO);
    app.world_mut()
        .get_mut::<VehicleBody>(other_tank)
        .unwrap()
        .net_id = 2;
    let jeep = crate::vehicle::tests::body(&mut app, "jeep", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(jeep).unwrap().net_id = 3;
    app.init_resource::<ReceivingFrame>()
        .init_resource::<NextNativeSeat>()
        .add_systems(Update, observe_input.in_set(PlayerUpdate))
        .add_systems(
            Update,
            adopt_native_seat.in_set(crate::vehicle::VehicleUpdate),
        );
    let template = app.world().resource::<Occupancy>().seat.unwrap();
    let accepted = |app: &mut App, seat: Option<(Entity, u32, u8)>| {
        let (net_id, seat_index) = seat.map_or((0, -1), |(_, id, index)| (id, i32::from(index)));
        app.world_mut().resource_mut::<GameSnapshot>().0 = serde_json::json!({
            "connected":true, "playerId":1,
            "player":{"alive":true,"vehicleNetId":net_id,"vehicleSeat":seat_index},
            "vehicleMounts":[{"vehicleNetId":net_id,"headOut":false}]
        });
        app.world_mut().resource_mut::<NextNativeSeat>().0 =
            seat.map(|(vehicle, net_id, seat)| crate::vehicle::OccupiedSeat {
                vehicle,
                net_id,
                seat,
                driver: seat == 0,
                ..template
            });
    };
    let check = |app: &App, mode, sensitivity, index, previous| {
        let observed = app.world().resource::<ReceivingFrame>();
        assert_eq!(observed.mode, mode);
        assert_eq!(observed.sensitivity, sensitivity);
        assert_eq!(observed.index, index);
        assert_eq!(
            observed.native_seat, previous,
            "reader ran before native seat adoption"
        );
    };
    accepted(&mut app, Some((tank, 1, 0)));
    app.update();
    // F2 adoption and an RMB edge arrive together. The old driver seat must not
    // discard the edge or apply unzoomed sensitivity to this frame's look input.
    accepted(&mut app, Some((tank, 1, 1)));
    aim(&mut app);
    check(&app, Mode::Gunsight, 1. / 12., 1, Some((1, 0)));
    assert!((fov(&app, camera) - 6.).abs() < 1e-5);

    accepted(&mut app, Some((tank, 1, 0)));
    aim(&mut app);
    check(&app, Mode::Periscope, 1., 1, Some((1, 1)));
    assert!((fov(&app, camera) - 72.).abs() < 1e-5);
    accepted(&mut app, Some((tank, 1, 1)));
    app.update();
    assert_eq!(app.world().resource::<TankOptics>().zoom(), 12.);

    accepted(&mut app, None);
    aim(&mut app);
    check(&app, Mode::None, 1., 1, Some((1, 1)));
    assert!((fov(&app, camera) - 72.).abs() < 1e-5);
    assert_eq!(overlay(&mut app).0, Visibility::Hidden);

    // Source _gunsightZoomIdx is player-owned: exits and entering a different
    // vehicle keep it, while a new WorldSessionEpoch resets it in input().
    accepted(&mut app, Some((other_tank, 2, 1)));
    app.update();
    check(&app, Mode::Gunsight, 1. / 12., 1, None);
    assert!((fov(&app, camera) - 6.).abs() < 1e-5);
    accepted(&mut app, Some((jeep, 3, 1)));
    aim(&mut app);
    check(&app, Mode::None, 1., 1, Some((2, 1)));

    accepted(&mut app, Some((tank, 1, 1)));
    app.update();
    // An authoritative empty registry must never reuse a cached occupied tank.
    app.world_mut().resource_mut::<GameSnapshot>().0 = serde_json::json!({
        "connected":true,"playerId":1,"player":{"alive":true},"vehicles":[]
    });
    app.world_mut().resource_mut::<NextNativeSeat>().0 = None;
    aim(&mut app);
    check(&app, Mode::None, 1., 1, Some((1, 1)));
}

#[test]
fn mounted_crosshair_uses_owned_crosshair_mount_in_fp_tp_and_suppresses_masked_gunner() {
    use crate::{
        entities::ItemCatalog,
        recoil::Recoil,
        ui::{UiModel, crosshair::CrosshairPlugin},
        vehicle::turrets::Operator,
        viewmodel::ViewmodelStatus,
    };
    let (mut app, vehicle, _) = app();
    app.init_resource::<UiModel>()
        .init_resource::<ItemCatalog>()
        .init_resource::<Recoil>()
        .init_resource::<ViewmodelStatus>()
        .init_resource::<Operator>()
        .add_plugins(CrosshairPlugin);
    app.world_mut().resource_mut::<UiModel>().connected = true;
    let type_id = app.world().get::<VehicleBody>(vehicle).unwrap().type_id;
    let adopt = |app: &mut App, driver: u32, gunner: u32, head_out: bool, seat: u8| {
        let state = serde_json::json!({"connected":true,"playerId":1,"player":{"alive":true},
            "vehicles":[{"netId":1,"typeId":type_id,"driverPlayerId":driver,"passengers":[gunner]}],
            "vehicleMounts":[{"vehicleNetId":1,"headOut":head_out,"mounts":[
                {"index":0,"yaw":0.,"pitch":0.,"ammo":60,"cooldownSeconds":0.,"crewAlive":true},
                {"index":1,"yaw":0.,"pitch":0.,"ammo":400,"cooldownSeconds":0.,"crewAlive":true}]}]});
        app.world_mut()
            .resource_mut::<Operator>()
            .refresh(&state, 0);
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        app.world_mut()
            .resource_mut::<Occupancy>()
            .seat
            .as_mut()
            .unwrap()
            .seat = seat;
        app.update();
    };
    let shown = |app: &mut App| {
        let root = app
            .world_mut()
            .query::<(Entity, &Name)>()
            .iter(app.world())
            .find(|(_, n)| n.as_str() == "Original third-person crosshair")
            .unwrap()
            .0;
        *app.world().get::<Visibility>(root).unwrap() == Visibility::Inherited
    };
    // Fallback driver owns the cannon continuously; no RMB is necessary.
    adopt(&mut app, 1, 0, false, 0);
    assert!(shown(&mut app));
    app.world_mut().resource_mut::<Perspective>().first_person = false;
    app.update();
    assert!(shown(&mut app));
    // An accepted gunner takes priority over the driver's selected slot.
    adopt(&mut app, 1, 2, false, 0);
    assert!(!shown(&mut app));
    app.world_mut().resource_mut::<Perspective>().first_person = true;
    adopt(&mut app, 2, 1, false, 1);
    assert!(!shown(&mut app));
    assert!(app.world().resource::<TankOptics>().gunsight_active());
    adopt(&mut app, 2, 1, true, 1);
    assert!(shown(&mut app));
    app.world_mut().resource_mut::<UiState>().screen = super::super::Screen::Paused;
    app.update();
    assert!(!shown(&mut app));
    app.world_mut().resource_mut::<UiState>().screen = super::super::Screen::Playing;
    app.world_mut().resource_mut::<WorldConfig>().fixed_camera = Some((Vec3::ZERO.into(), 0., 0.));
    adopt(&mut app, 2, 1, false, 1);
    assert_eq!(
        app.world().resource::<TankOptics>().mode,
        Mode::None,
        "outside fixture hides the optic"
    );
    assert!(
        app.world().resource::<TankOptics>().gunsight_active(),
        "source logical gate still applies"
    );
    assert!(
        !shown(&mut app),
        "no false generic mark under the hidden source optic"
    );
    app.world_mut().resource_mut::<UiModel>().alive = false;
    adopt(&mut app, 2, 1, true, 1);
    assert!(!shown(&mut app));
}
