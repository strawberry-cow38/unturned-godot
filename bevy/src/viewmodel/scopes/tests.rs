use super::*;
#[test]
fn newly_activated_scope_has_shadow_cascades_in_its_first_render_frame() {
    // This exercises Bevy's actual shadow builder. Activating the extra view
    // after it runs makes prepare_lights panic on a missing cascade entry.
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), TransformPlugin))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_resource::<WorldConfig>()
        .init_resource::<bevy::light::DirectionalLightShadowMap>()
        .add_systems(
            PostUpdate,
            bevy::light::cascade::build_directional_light_cascades
                .in_set(bevy::light::SimulationLightSystems::UpdateDirectionalLightCascades)
                .after(bevy::transform::TransformSystems::Propagate),
        )
        .add_plugins(ScopePlugin);
    app.world_mut()
        .spawn((PlayerCamera, Transform::from_xyz(8., 2., 3.)));
    app.world_mut()
        .spawn((ViewmodelCamera, Camera::default(), Transform::IDENTITY));
    app.world_mut().spawn((
        ScopeMount {
            calibration: calibration("scope_8x_sight.txt").unwrap(),
            reticle: None,
        },
        Transform::IDENTITY,
    ));
    let sun = app
        .world_mut()
        .spawn((
            DirectionalLight {
                shadow_maps_enabled: true,
                ..default()
            },
            Transform::IDENTITY,
        ))
        .id();
    app.update();
    let scope = app
        .world_mut()
        .query_filtered::<Entity, With<ScopeView>>()
        .single(app.world())
        .unwrap();
    assert!(app.world().get::<Camera>(scope).unwrap().is_active);
    let cascades = app.world().get::<bevy::light::Cascades>(sun).unwrap();
    assert!(!cascades.cascades[&scope].is_empty());
}
#[test]
fn source_scope_axes_zoom_masks_and_integral_reticle_are_preserved() {
    for (mesh, zoom, sides) in [
        ("scope_8x_sight.txt", 8., 12),
        ("scope_16x_sight.txt", 90. / 5.63, 12),
        ("makeshift_scope_sight.txt", 6., 6),
        ("shadowstalker_scope_sight.txt", 6., 4),
        ("cross_scope_sight.txt", 6., 12),
        ("chevron_scope_sight.txt", 4., 12),
        ("augewehr_sight.txt", 4., 12),
    ] {
        let c = calibration(mesh).unwrap();
        assert!((90. / c.fov - zoom).abs() < 0.001);
        assert_eq!(c.sides, sides);
        assert_eq!(c.lens.x, c.objective.x);
        assert_eq!(c.lens.z, c.objective.z);
        assert!(c.objective.y > c.lens.y);
        if !c.integral {
            assert_eq!(c.aim.x, c.lens.x);
            assert_eq!(c.aim.z, c.lens.z);
            assert!(c.aim.y < c.lens.y);
        }
        assert!((basis() * Vec3::NEG_Z).abs_diff_eq(Vec3::Y, 1e-6));
        assert!((basis() * Vec3::Y).abs_diff_eq(Vec3::NEG_Z, 1e-6));
    }
    assert_eq!(
        glass_mask(calibration("cross_scope_sight.txt").unwrap()).zw(),
        Vec2::new(0.056, 1.)
    );
    assert_eq!(glass_mask(calibration("augewehr_sight.txt").unwrap()).w, 2.);
    assert_eq!(
        glass_mask(calibration("shadowstalker_scope_sight.txt").unwrap()).y,
        0.
    );
    assert!(calibration("red_dot_sight.txt").is_none());
}
#[test]
fn objective_follows_rigid_recoil_and_camera_relative_translation_with_no_zeroing() {
    let c = calibration("scope_8x_sight.txt").unwrap();
    let main = GlobalTransform::from(
        Transform::from_xyz(1024., 20., -30.).with_rotation(Quat::from_rotation_y(0.7)),
    );
    let view = GlobalTransform::from(
        Transform::from_xyz(30., 2., 10.).with_rotation(Quat::from_rotation_z(0.2)),
    );
    let part = Transform::from_xyz(0.1, -0.15, -0.5).with_rotation(Quat::from_euler(
        EulerRot::XYZ,
        0.15,
        0.2,
        0.3,
    ));
    let mount = view * part;
    let mapped = mapped_objective(&main, &view, &mount, c);
    let expected = main.transform_point(part.transform_point(c.objective));
    assert!(mapped.translation.abs_diff_eq(expected, 0.0002));
    assert!(
        (mapped.rotation * Vec3::NEG_Z)
            .abs_diff_eq(main.rotation() * part.rotation * Vec3::Y, 1e-5)
    );
    // The lens camera starts at the objective, not at the player's eye.
    assert!(mapped.translation.distance(main.translation()) > 0.1);
}
#[test]
fn removing_mount_cleans_camera_and_hidden_viewmodel_disables_scope_without_ads_gate() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .add_systems(Update, position);
    let main = app
        .world_mut()
        .spawn((PlayerCamera, GlobalTransform::IDENTITY))
        .id();
    let vm = app
        .world_mut()
        .spawn((
            ViewmodelCamera,
            Camera::default(),
            GlobalTransform::IDENTITY,
        ))
        .id();
    let mount = app
        .world_mut()
        .spawn((
            ScopeMount {
                calibration: calibration("scope_8x_sight.txt").unwrap(),
                reticle: None,
            },
            GlobalTransform::IDENTITY,
        ))
        .id();
    let view = app
        .world_mut()
        .spawn((
            ScopeView {
                mount,
                integral: false,
            },
            Transform::IDENTITY,
            GlobalTransform::IDENTITY,
            Camera::default(),
        ))
        .id();
    app.update();
    assert!(app.world().get::<Camera>(view).unwrap().is_active);
    app.world_mut().get_mut::<Camera>(vm).unwrap().is_active = false;
    app.update();
    assert!(!app.world().get::<Camera>(view).unwrap().is_active);
    app.world_mut().entity_mut(main).insert(DistanceFog {
        color: Color::srgb(0.1, 0.2, 0.3),
        ..default()
    });
    app.update();
    assert_eq!(
        app.world().get::<DistanceFog>(view).unwrap().color,
        Color::srgb(0.1, 0.2, 0.3)
    );
    app.world_mut().despawn(mount);
    app.update();
    assert!(app.world().get_entity(view).is_err());
}
#[test]
fn mounted_zoom_scales_input_continuously_and_keeps_iron_sensitivity() {
    assert_eq!(sensitivity(0., 16.), 1.);
    assert!((sensitivity(1., 16.) - 1. / 16.).abs() < 1e-6);
    assert!((sensitivity(0.5, 8.) - (1. + 1. / 8.) / 2.).abs() < 1e-6);
    assert!((sensitivity(1., 0.) - 0.65).abs() < 1e-6);
}
#[test]
fn actual_mount_builds_one_linear_target_with_transparent_missing_reticle() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Image>()
        .init_asset::<Mesh>()
        .init_asset::<GlassMaterial>()
        .init_resource::<WorldConfig>()
        .add_systems(Update, build);
    let mount = app
        .world_mut()
        .spawn(ScopeMount {
            calibration: calibration("scope_8x_sight.txt").unwrap(),
            reticle: Some("intentionally_missing_scope_reticle.png".into()),
        })
        .id();
    app.update();
    app.update();
    let cameras: Vec<_> = app
        .world_mut()
        .query::<(
            &ScopeView,
            &RenderTarget,
            &Projection,
            Has<Hdr>,
            &Tonemapping,
        )>()
        .iter(app.world())
        .map(|(s, t, p, h, tm)| (s.mount, t.clone(), p.clone(), h, *tm))
        .collect();
    assert_eq!(cameras.len(), 1);
    let (owner, target, projection, hdr, tonemap) = &cameras[0];
    assert_eq!(*owner, mount);
    assert!(*hdr);
    assert_eq!(*tonemap, Tonemapping::None);
    let RenderTarget::Image(target) = target else {
        panic!("scope must render into its own target")
    };
    let image = app
        .world()
        .resource::<Assets<Image>>()
        .get(&target.handle)
        .unwrap();
    assert_eq!(image.texture_descriptor.format, TextureFormat::Rgba16Float);
    assert_eq!(image.size(), UVec2::splat(720));
    let Projection::Perspective(p) = projection else {
        panic!()
    };
    assert!((p.fov - 11.25_f32.to_radians()).abs() < 1e-6);
    let materials = app.world().resource::<Assets<GlassMaterial>>();
    assert_eq!(materials.len(), 1);
    let glass = materials.iter().next().unwrap().1;
    assert!(!glass.base.fog_enabled);
    assert!(glass.base.unlit);
    assert!(glass.base.cull_mode.is_none());
    let blank = app
        .world()
        .resource::<Assets<Image>>()
        .get(&glass.extension.reticle)
        .unwrap();
    assert_eq!(blank.data.as_deref(), Some(&[0, 0, 0, 0][..]));
}
