use super::*;

#[test]
fn pregame_subpanels_keep_actual_menu_camera_and_local_pause_policy() {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .init_resource::<crate::world::origin::WorldOrigin>()
    .insert_resource(crate::world::WorldConfig {
        fixed_camera: None,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<UiState>()
    .init_resource::<PlayerInput>()
    .init_resource::<CraftingEye>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<crate::avatar::Perspective>()
    .insert_resource(WorldState {
        focus: Vec3::new(100., 30., -70.),
        ..default()
    })
    .add_systems(Update, camera_follow);
    origin_test_player(app.world_mut(), Vec3::new(-520., 31., 63.), 0);
    let camera = app
        .world_mut()
        .spawn((
            PlayerCamera,
            Transform::IDENTITY,
            Projection::Perspective(default()),
        ))
        .id();
    app.update();
    let menu = *app.world().get::<Transform>(camera).unwrap();
    for screen in [
        Screen::Appearance,
        Screen::DirectConnect,
        Screen::Servers,
        Screen::Host,
        Screen::Settings,
    ] {
        app.world_mut().resource_mut::<UiState>().screen = screen;
        app.update();
        assert_eq!(
            *app.world().get::<Transform>(camera).unwrap(),
            menu,
            "{screen:?}"
        );
        assert!(simulation_paused(app.world().resource::<UiState>()));
    }
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.update();
    assert!(
        (app.world().get::<Transform>(camera).unwrap().translation - menu.translation).length()
            > 100.
    );
    assert!(!simulation_paused(app.world().resource::<UiState>()));
}
