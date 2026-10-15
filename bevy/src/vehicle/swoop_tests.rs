use super::*;

#[test]
fn departing_map_blocks_live_drive_axes_but_preserves_discrete_ignition() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let mut model = UiModel::default();
    model.connected = true;
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(bridge)
    .init_resource::<Settings>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        map_closing: true,
        ..default()
    })
    .insert_resource(model)
    .init_resource::<ContentCache>()
    .init_resource::<WorldConfig>()
    .init_resource::<AccumulatedMouseMotion>()
    .init_resource::<turrets::Operator>()
    .add_systems(Update, read_controls);
    let car = tests::body(&mut app, "police", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(car).unwrap().flags = 0;
    app.insert_resource(Occupancy {
        seat: Some(OccupiedSeat {
            vehicle: car,
            net_id: 1,
            seat: 0,
            eye_pose: Transform::IDENTITY,
            body_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: true,
            aircraft: false,
        }),
    });
    let settings = app.world().resource::<Settings>().clone();
    {
        let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
        for key in [KeyCode::KeyW, KeyCode::KeyD, KeyCode::Space, KeyCode::KeyN] {
            keys.press(key);
        }
    }
    assert!(settings.just_pressed(
        GameAction::VehicleIgnition,
        app.world().resource::<ButtonInput<KeyCode>>(),
        app.world().resource::<ButtonInput<MouseButton>>()
    ));
    app.update();
    let body = app.world().get::<VehicleBody>(car).unwrap();
    assert_eq!((body.controls.throttle, body.controls.steer), (0., 0.));
    assert!(!body.controls.handbrake);
    assert!(
        body.controls.engine,
        "source discrete ignition remains available while the map departs"
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.world_mut().resource_mut::<UiState>().map_closing = false;
    app.update();
    let body = app.world().get::<VehicleBody>(car).unwrap();
    assert_eq!((body.controls.throttle, body.controls.steer), (1., 1.));
    assert!(body.controls.handbrake);
}
