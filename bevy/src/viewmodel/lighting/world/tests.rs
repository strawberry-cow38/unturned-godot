use super::*;
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin));
    install(&mut app);
    app
}
fn scan_now(app: &mut App) {
    app.world_mut().resource_mut::<Scan>().due = true;
    app.update();
}
fn point(app: &mut App, position: Vec3, energy: f32, range: f32, color: Color) -> Entity {
    app.world_mut()
        .spawn((
            WorldLight,
            PointLight {
                intensity: crate::hdr::point_lumens(energy),
                range,
                color,
                ..default()
            },
            Transform::from_translation(position),
        ))
        .id()
}
#[test]
fn tagged_nearest_four_mirrors_follow_camera_basis_and_hidden_ancestors() {
    let mut app = app();
    let pose = Transform::from_xyz(90., 3., -200.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        1.2,
        -0.3,
        0.1,
    ));
    let main = app.world_mut().spawn((PlayerCamera, pose)).id();
    // Nonidentity body/head split: mirror space is the view camera, not arm frame.
    let frame = app
        .world_mut()
        .spawn((
            Transform::from_rotation(Quat::from_rotation_y(-0.5)),
            ChildOf(main),
        ))
        .id();
    let camera = app
        .world_mut()
        .spawn((
            ViewmodelCamera,
            Transform::from_rotation(Quat::from_rotation_y(0.5)),
            Visibility::Inherited,
            ChildOf(frame),
        ))
        .id();
    let mut sources = Vec::new();
    for i in 1..=6 {
        let at = pose.transform_point(Vec3::new(i as f32, 0., -1.));
        sources.push(point(&mut app, at, 2., 20., Color::srgb(0.3, 0.6, 0.9)));
    }
    // Untagged player eye lamps must never be mirrored even when closer.
    app.world_mut().spawn((PointLight::default(), pose));
    let hidden = app
        .world_mut()
        .spawn((Transform::IDENTITY, Visibility::Hidden))
        .id();
    let rejected = point(&mut app, pose.translation, 100., 100., Color::WHITE);
    app.world_mut()
        .entity_mut(rejected)
        .insert((ChildOf(hidden), Visibility::Visible));
    scan_now(&mut app);
    let mirror_ids = app.world().resource::<Scan>().mirrors.clone();
    assert_eq!(mirror_ids.len(), 4);
    for (i, mirror) in mirror_ids.iter().enumerate() {
        let expected = app
            .world()
            .get::<GlobalTransform>(sources[i])
            .unwrap()
            .translation();
        let actual = app
            .world()
            .get::<GlobalTransform>(*mirror)
            .unwrap()
            .translation();
        assert!(actual.abs_diff_eq(expected, 3e-5), "{actual} != {expected}");
        let light = app.world().get::<PointLight>(*mirror).unwrap();
        assert!(!light.shadow_maps_enabled);
        assert_eq!(light.intensity, crate::hdr::point_lumens(2.));
        assert_eq!(
            *app.world().get::<RenderLayers>(*mirror).unwrap(),
            RenderLayers::layer(LAYER)
        );
        assert!(app.world().get::<WorldLight>(*mirror).is_none());
        assert_eq!(
            app.world().get::<ChildOf>(*mirror).unwrap().parent(),
            camera
        );
    }
    // Stable pool shrinks by hiding unused entries and grows without duplicates.
    for source in &sources {
        app.world_mut()
            .entity_mut(*source)
            .insert(Visibility::Hidden);
    }
    scan_now(&mut app);
    for id in &mirror_ids {
        assert_eq!(
            *app.world().get::<Visibility>(*id).unwrap(),
            Visibility::Hidden
        );
    }
    assert_eq!(app.world().resource::<NearbyLight>().energy, 0.);
    app.world_mut()
        .entity_mut(sources[0])
        .insert(Visibility::Inherited);
    scan_now(&mut app);
    assert_eq!(app.world().resource::<Scan>().mirrors, mirror_ids);
    assert_eq!(
        *app.world().get::<Visibility>(mirror_ids[0]).unwrap(),
        Visibility::Inherited
    );
    // World origin translation leaves camera-local mirror offsets unchanged.
    let local = *app.world().get::<Transform>(mirror_ids[0]).unwrap();
    let shift = Vec3::new(1024., 0., -256.);
    app.world_mut()
        .get_mut::<Transform>(main)
        .unwrap()
        .translation -= shift;
    for source in &sources {
        app.world_mut()
            .get_mut::<Transform>(*source)
            .unwrap()
            .translation -= shift;
    }
    scan_now(&mut app);
    assert!(
        app.world()
            .get::<Transform>(mirror_ids[0])
            .unwrap()
            .translation
            .abs_diff_eq(local.translation, 1e-4)
    );
}
#[test]
fn unarmed_stage_averages_every_candidate_in_source_color_space_and_caps_energy() {
    let mut app = app();
    app.world_mut().spawn((PlayerCamera, Transform::IDENTITY));
    // All six contribute even though only four could ever be mirrored.
    for i in 0..6 {
        point(
            &mut app,
            Vec3::X * 5.,
            2.,
            10.,
            if i < 2 {
                Color::srgb(1., 0., 0.)
            } else {
                Color::srgb(0., 0., 1.)
            },
        );
    }
    point(&mut app, Vec3::X * 11., 40., 10., Color::WHITE); // candidate, level weight0
    point(&mut app, Vec3::ZERO, 0.01, 10., Color::WHITE); // exact threshold excluded
    point(&mut app, Vec3::X * 20., 40., 10., Color::WHITE); // exact2range excluded
    scan_now(&mut app);
    let nearby = app.world().resource::<NearbyLight>();
    assert!((nearby.energy - 3.).abs() < 1e-6);
    let rgb = nearby.color.to_srgba();
    assert!((rgb.red - 1. / 3.).abs() < 1e-6 && (rgb.blue - 2. / 3.).abs() < 1e-6);
    assert_eq!(rgb.green, 0.);
    assert!(app.world().resource::<Scan>().mirrors.is_empty());
    point(&mut app, Vec3::ZERO, 20., 10., Color::WHITE);
    scan_now(&mut app);
    assert_eq!(app.world().resource::<NearbyLight>().energy, 4.);
}
#[test]
fn scan_runs_immediately_then_every_five_simulation_ticks_and_stops_when_paused() {
    let mut app = app();
    app.world_mut().spawn((PlayerCamera, Transform::IDENTITY));
    app.world_mut().run_schedule(FixedLast);
    assert!(app.world().resource::<Scan>().due);
    app.world_mut().resource_mut::<Scan>().due = false;
    for _ in 0..4 {
        app.world_mut().run_schedule(FixedLast);
        assert!(!app.world().resource::<Scan>().due);
    }
    app.world_mut().run_schedule(FixedLast);
    assert!(app.world().resource::<Scan>().due);
    app.insert_resource(crate::ui::UiState {
        screen: crate::ui::Screen::Paused,
        ..default()
    });
    app.world_mut().resource_mut::<Scan>().due = false;
    for _ in 0..10 {
        app.world_mut().run_schedule(FixedLast);
    }
    assert!(!app.world().resource::<Scan>().due);
}

#[test]
fn replacing_viewmodel_camera_rebuilds_its_despawned_light_pool() {
    let mut app = app();
    app.world_mut().spawn((PlayerCamera, Transform::IDENTITY));
    point(&mut app, Vec3::new(1., 0., -2.), 2., 10., Color::WHITE);
    let camera = app
        .world_mut()
        .spawn((ViewmodelCamera, Transform::IDENTITY))
        .id();
    scan_now(&mut app);
    let old = app.world().resource::<Scan>().mirrors[0];
    app.world_mut().entity_mut(camera).despawn();
    assert!(app.world().get_entity(old).is_err());
    scan_now(&mut app);
    assert!(
        app.world().resource::<NearbyLight>().energy > 0.,
        "unarmed stage light continues without a viewport"
    );
    let camera = app
        .world_mut()
        .spawn((ViewmodelCamera, Transform::IDENTITY))
        .id();
    scan_now(&mut app);
    let state = app.world().resource::<Scan>();
    assert_eq!(state.camera, Some(camera));
    assert_eq!(state.mirrors.len(), 1);
    let new = state.mirrors[0];
    assert_ne!(new, old);
    assert_eq!(app.world().get::<ChildOf>(new).unwrap().parent(), camera);
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<Mirror>>()
            .iter(app.world())
            .count(),
        1
    );
}
