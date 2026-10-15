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
    .add_systems(Update, (input, reset_camera, apply).chain());
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
