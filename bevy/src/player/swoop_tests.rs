use super::*;

#[test]
fn departing_map_keeps_real_input_and_cursor_blocked_until_closed() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let mut model = UiModel::default();
    model.connected = true;
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .insert_resource(AccumulatedMouseMotion {
            delta: Vec2::new(4., 3.),
        })
        .init_resource::<Settings>()
        .init_resource::<crate::viewmodel::ViewmodelStatus>()
        .init_resource::<crate::recoil::Recoil>()
        .init_resource::<GameSnapshot>()
        .init_resource::<crate::entities::ItemCatalog>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(UiState {
            screen: Screen::Playing,
            map_closing: true,
            ..default()
        })
        .insert_resource(model)
        .insert_resource(PlayerInput {
            yaw: 30.,
            pitch: 10.,
            ..default()
        })
        .add_systems(Update, read_input);
    let window = app
        .world_mut()
        .spawn((Window::default(), CursorOptions::default(), PrimaryWindow))
        .id();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyW);
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .press(MouseButton::Left);
    app.update();
    let input = app.world().resource::<PlayerInput>();
    assert_eq!((input.yaw, input.pitch), (30., 10.));
    assert_eq!(input.movement, Vec2::ZERO);
    assert!(!input.fire && !input.fire_pressed);
    let cursor = app.world().get::<CursorOptions>(window).unwrap();
    assert!(cursor.visible);
    assert_eq!(cursor.grab_mode, CursorGrabMode::None);
    app.world_mut().resource_mut::<UiState>().map_closing = false;
    app.world_mut()
        .resource_mut::<ButtonInput<MouseButton>>()
        .reset_all();
    app.update();
    let input = app.world().resource::<PlayerInput>();
    assert_ne!(input.yaw, 30.);
    assert_eq!(input.movement, Vec2::Y);
    let cursor = app.world().get::<CursorOptions>(window).unwrap();
    assert!(!cursor.visible);
    assert_eq!(cursor.grab_mode, CursorGrabMode::Locked);
}
