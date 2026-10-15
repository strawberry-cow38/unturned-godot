use super::*;

#[test]
fn portrait_light_order_retains_room_tint_but_source_sun_replaces_energy_and_ambient() {
    let mut lights = Lighting::default();
    let room = NearbyLight {
        energy: 2.,
        color: Color::srgb(1., 0.1, 0.1),
    };
    let sun = DirectionalLight {
        color: Color::srgb(0.9, 0.8, 0.7),
        illuminance: crate::hdr::directional_illuminance(0.1),
        ..default()
    };
    let ambient = GlobalAmbientLight {
        color: Color::srgb(0.1, 0.2, 0.3),
        brightness: crate::hdr::ambient_brightness(0.1),
        ..default()
    };
    lights.source(Some(&room), Some(&sun), Some(&ambient));
    assert!((lights.key_energy - 0.38).abs() < 1e-6);
    assert!((lights.fill_energy - 0.35 * 0.38 / 0.75).abs() < 1e-6);
    assert!((lights.ambient_energy - 0.45).abs() < 1e-6);
    assert_eq!(lights.ambient, ambient.color);
    assert_eq!(lights.key, sun.color);
    assert!((lights.fill.to_srgba().red - (0.88 * 0.3 + 1. * 0.7)).abs() < 1e-6);
    assert!((lights.fill.to_srgba().green - (0.88 * 0.3 + 0.1 * 0.7)).abs() < 1e-6);
    let tint = lights.fill.to_srgba();
    lights.source(Some(&room), Some(&sun), Some(&ambient));
    assert!(
        lights.fill.to_srgba().red > tint.red,
        "source lerps existing fill each frame"
    );

    let mut isolated = Lighting::default();
    isolated.source(Some(&room), None, Some(&ambient));
    assert!((isolated.fill_energy - 0.95).abs() < 1e-6);
    assert!((isolated.ambient_energy - 1.24).abs() < 1e-6);
    assert_ne!(
        isolated.ambient, ambient.color,
        "no source Sun means no world-environment adoption"
    );
}

#[test]
fn real_body_bounds_frame_once_independent_of_gun_and_target_resize_retains_image() {
    let mut app = super::super::tests::app();
    app.init_asset::<PortraitMaterial>()
        .init_resource::<Surface>()
        .add_systems(
            PostUpdate,
            configure.after(bevy::transform::TransformSystems::Propagate),
        );
    app.world_mut().resource_mut::<Surface>().size = UVec2::new(616, 444);
    app.update();
    let surface = app.world().resource::<Surface>();
    let camera = surface.camera.unwrap();
    let image = surface.image.clone().unwrap();
    let material = surface.material.clone().unwrap();
    let root = app.world().resource::<Portrait>().root.unwrap();
    let data = &app
        .world()
        .get::<RigPlayback>(root)
        .unwrap()
        .data
        .mesh
        .positions;
    let low = data.iter().map(|p| p[1]).fold(f32::INFINITY, f32::min);
    let high = data.iter().map(|p| p[1]).fold(f32::NEG_INFINITY, f32::max);
    let transform = *app.world().get::<Transform>(camera).unwrap();
    assert_eq!(
        *app.world().get::<GlobalTransform>(camera).unwrap(),
        GlobalTransform::from(transform),
        "first visible frame must not extract an identity camera after propagation"
    );
    assert!((transform.translation.y - ((low + high) * 0.5 + 0.05)).abs() < 1e-6);
    assert!(
        (transform.translation.z - ((high - low) * 1.34 * 0.5 / 17_f32.to_radians().tan())).abs()
            < 1e-6
    );
    assert_eq!(
        *app.world().get::<RenderLayers>(camera).unwrap(),
        RenderLayers::layer(LAYER)
    );
    assert_eq!(*app.world().get::<Msaa>(camera).unwrap(), Msaa::Sample4);
    assert!(app.world().get::<Hdr>(camera).is_some());
    assert_eq!(
        *app.world().get::<Tonemapping>(camera).unwrap(),
        Tonemapping::None
    );
    let original_lights = app
        .world_mut()
        .query_filtered::<Entity, With<PortraitLight>>()
        .iter(app.world())
        .count();
    assert_eq!(original_lights, 2);
    let mut lights = app
        .world_mut()
        .query_filtered::<(&Transform, &GlobalTransform), With<PortraitLight>>();
    for (local, global) in lights.iter(app.world()) {
        assert_eq!(
            *global,
            GlobalTransform::from(*local),
            "first portrait light direction must be authored"
        );
    }

    app.world_mut().resource_mut::<Surface>().size = UVec2::new(616, 600);
    app.world_mut()
        .resource_mut::<crate::player::GameSnapshot>()
        .0["player"]["heldItemId"] = serde_json::json!(4);
    app.world_mut().resource_mut::<Portrait>().yaw = 1.2;
    app.update();
    assert_eq!(*app.world().get::<Transform>(camera).unwrap(), transform);
    let surface = app.world().resource::<Surface>();
    assert_eq!(surface.camera, Some(camera));
    assert_eq!(surface.image.as_ref(), Some(&image));
    assert_eq!(surface.material.as_ref(), Some(&material));
    assert_eq!(
        app.world()
            .resource::<Assets<Image>>()
            .get(&image)
            .unwrap()
            .size(),
        UVec2::new(616, 600)
    );
    app.world_mut().resource_mut::<Portrait>().active = false;
    app.update();
    assert!(!app.world().get::<Camera>(camera).unwrap().is_active);
    app.world_mut().resource_mut::<Portrait>().active = true;
    app.update();
    assert!(app.world().get::<Camera>(camera).unwrap().is_active);
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<PortraitLight>>()
            .iter(app.world())
            .count(),
        2
    );
    app.world_mut()
        .resource_mut::<crate::player::GameSnapshot>()
        .0["connected"] = serde_json::json!(false);
    app.update();
    assert!(!app.world().get::<Camera>(camera).unwrap().is_active);
}
