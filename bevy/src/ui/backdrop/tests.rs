use super::*;
#[test]
fn routes_only_requested_roots_and_recovers_after_camera_replacement() {
    let mut app = App::new();
    app.add_systems(Startup, spawn_camera)
        .add_systems(PostUpdate, route);
    let hud = app.world_mut().spawn(Node::default()).id();
    let modal = app
        .world_mut()
        .spawn((Node::default(), Backdrop::ammo_pie(0.7)))
        .id();
    app.update();
    let camera = app.world().get::<UiTargetCamera>(modal).unwrap().0;
    assert_eq!(
        app.world().get::<Settings>(camera).unwrap().parameters.x,
        0.7
    );
    assert_eq!(app.world().get::<Camera>(camera).unwrap().order, 3);
    assert!(app.world().get::<UiTargetCamera>(hud).is_none());
    app.world_mut().entity_mut(camera).despawn();
    app.update();
    let replacement = app.world().get::<UiTargetCamera>(modal).unwrap().0;
    assert_ne!(replacement, camera);
    assert!(app.world().get::<ModalCamera>(replacement).is_some());
    app.world_mut().entity_mut(modal).remove::<Backdrop>();
    app.update();
    assert!(app.world().get::<UiTargetCamera>(modal).is_none());
    assert_eq!(
        app.world()
            .get::<Settings>(replacement)
            .unwrap()
            .parameters
            .x,
        0.
    );
    app.world_mut()
        .entity_mut(modal)
        .insert(Backdrop::ammo_pie(1.));
    app.update();
    app.world_mut().entity_mut(modal).despawn();
    app.update();
    assert_eq!(
        app.world()
            .get::<Settings>(replacement)
            .unwrap()
            .parameters
            .x,
        0.
    );
}
#[test]
fn backdrop_uses_source_lod_spread_and_eased_fade_without_a_second_clock() {
    assert_eq!(Backdrop::ammo_pie(f32::NAN).fade, 0.);
    assert_eq!(Backdrop::ammo_pie(-1.).fade, 0.);
    assert_eq!(Backdrop::ammo_pie(2.).fade, 1.);
    let mut app = App::new();
    app.add_systems(PostUpdate, route);
    app.world_mut().spawn(Backdrop::ammo_pie(0.875));
    app.update();
    let settings = app
        .world_mut()
        .query::<&Settings>()
        .single(app.world())
        .unwrap();
    assert_eq!(settings.parameters, Vec4::new(0.875, 1.8, 3., 0.));
    app.update();
    let settings = app
        .world_mut()
        .query::<&Settings>()
        .single(app.world())
        .unwrap();
    assert_eq!(settings.parameters.x, 0.875);
}

mod visual;
