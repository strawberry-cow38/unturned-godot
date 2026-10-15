use super::*;
use serde_json::json;
#[test]
fn live_key_switches_obey_busy_ui_and_fit_lifecycles() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<AudioSource>()
        .init_resource::<Switches>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<ItemCatalog>()
        .init_resource::<Settings>()
        .init_resource::<WorldConfig>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .insert_resource(ViewmodelStatus {
            held: 4,
            ready: true,
            ..default()
        })
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .insert_resource(GameSnapshot(
            json!({"connected":true,"player":{"alive":true,"heldItemId":4},
            "weapon":{"selectionVersion":1,"attachments":{"tacticalId":151}},
            "inventory":{"worn":{"glasses":1199}}}),
        ))
        .add_systems(Update, input);
    let press = |app: &mut App| {
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .release_all();
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .clear();
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .press(KeyCode::KeyN);
        app.update();
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .clear();
    };
    press(&mut app);
    assert!(app.world().resource::<Switches>().tactical_lit);
    assert!(!app.world().resource::<Switches>().head_lit);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["reloading"] = json!(true);
    press(&mut app);
    assert!(app.world().resource::<Switches>().tactical_lit);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["attachments"]["tacticalId"] =
        json!(152);
    app.update();
    assert!(!app.world().resource::<Switches>().tactical_lit);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["attachments"]["tacticalId"] =
        json!(1008);
    press(&mut app);
    assert!(app.world().resource::<Switches>().head_lit);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    press(&mut app);
    assert!(app.world().resource::<Switches>().head_lit);
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["alive"] = json!(false);
    app.update();
    assert!(!app.world().resource::<Switches>().head_lit);
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["alive"] = json!(true);
    app.update();
    assert!(app.world().resource::<Switches>().head_lit);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(!app.world().resource::<Switches>().head_lit);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.insert_resource(crate::vehicle::Occupancy {
        seat: Some(crate::vehicle::OccupiedSeat {
            vehicle: Entity::PLACEHOLDER,
            net_id: 1,
            seat: 0,
            body_pose: Transform::IDENTITY,
            eye_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: true,
            aircraft: false,
        }),
    });
    press(&mut app);
    assert!(
        !app.world().resource::<Switches>().head_lit,
        "driver ignition consumes its shared N binding"
    );
    app.world_mut()
        .resource_mut::<crate::vehicle::Occupancy>()
        .seat
        .as_mut()
        .unwrap()
        .driver = false;
    press(&mut app);
    assert!(
        app.world().resource::<Switches>().head_lit,
        "passengers can still switch their own lamp"
    );
    app.world_mut()
        .resource_mut::<crate::vehicle::Occupancy>()
        .seat
        .as_mut()
        .unwrap()
        .driver = true;
    app.world_mut().resource_mut::<Settings>().bindings.insert(
        GameAction::VehicleIgnition,
        crate::settings::Binding::Key("KeyI".into()),
    );
    press(&mut app);
    assert!(
        !app.world().resource::<Switches>().head_lit,
        "a separately bound ignition leaves the lamp key available"
    );
}
#[test]
fn source_priority_and_busy_gates_preserve_glasses_fallthrough() {
    assert_eq!(toggle_target(151, 334), Some(Toggle::Rail));
    assert_eq!(toggle_target(152, 1199), Some(Toggle::Rail));
    assert_eq!(toggle_target(1008, 334), Some(Toggle::Vision));
    assert_eq!(toggle_target(1438, 1199), Some(Toggle::Head));
    assert_eq!(toggle_target(0, 0), None);
    assert!(!ready_to_switch(Toggle::Rail, false, false, false));
    assert!(!ready_to_switch(Toggle::Rail, true, true, false));
    assert!(!ready_to_switch(Toggle::Rail, true, false, true));
    assert!(ready_to_switch(Toggle::Head, false, true, true));
}
#[test]
fn lamp_uses_actual_flashlight_dat_defaults_and_headlamp_is_stronger_tighter() {
    let bridge =
        crate::bridge::CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR")))
            .unwrap();
    bridge
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root}),
        )
        .unwrap();
    let response = bridge.request("catalog", json!({})).unwrap();
    let items: Vec<crate::entities::ItemVisual> =
        serde_json::from_value(response["result"]["items"].clone()).unwrap();
    let catalog = ItemCatalog {
        items: items.into_iter().map(|i| (i.id, i)).collect(),
        ..default()
    };
    let rail = torch(&catalog);
    let handheld = spot(&catalog.items[&276].melee);
    assert_eq!(rail.range, handheld.range);
    assert_eq!(rail.energy, handheld.energy);
    assert_eq!(rail.color, handheld.color);
    assert_eq!(rail.cone, handheld.cone);
    assert_eq!(rail.range, 64.);
    assert_eq!(rail.cone, 45_f32.to_radians());
    let head = headlamp();
    assert!(head.range > rail.range && head.energy > rail.energy && head.cone < rail.cone);
}
#[test]
fn ray_beam_ends_at_surface_and_dot_tapers_with_distance_without_vertical_nan() {
    for hit in [Vec3::new(2., 1., -10.), Vec3::Y * 10., Vec3::NEG_Y * 10.] {
        let start = Vec3::new(0.1, -0.06, -0.35);
        let pose = beam_pose(start, hit).unwrap();
        assert!(pose.transform_point(Vec3::Y * 0.5).abs_diff_eq(hit, 1e-5));
        assert!(
            pose.transform_point(Vec3::NEG_Y * 0.5)
                .abs_diff_eq(start, 1e-5)
        );
    }
    assert!(beam_pose(Vec3::ZERO, Vec3::ZERO).is_none());
    let near = dot_size(10., 60_f32.to_radians());
    let far = dot_size(100., 60_f32.to_radians());
    assert!(far > near && far < near * 10.);
}
#[test]
fn extracted_lens_mask_keeps_near_duplicate_bulb_pixels_and_blacks_housing() {
    let source = Image::new(
        Extent3d {
            width: 4,
            height: 1,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        vec![
            247, 224, 148, 255, 246, 223, 147, 255, 73, 73, 73, 255, 0, 0, 0, 255,
        ],
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    let mask = emission_mask(&source).unwrap();
    assert_eq!(
        mask.data.unwrap(),
        [
            247, 224, 148, 255, 246, 223, 147, 255, 0, 0, 0, 255, 0, 0, 0, 255
        ]
    );
    let image = soft_disc();
    let data = image.data.unwrap();
    assert_eq!(data[3], 0);
    assert!(data[(32 * 64 + 32) * 4 + 3] > 250);
    // Equal perceived brightness but different hues: the original first pixel
    // wins. Picking the last maximum would turn the wrong lens colour on.
    let tied = Image::new(
        Extent3d {
            width: 2,
            height: 1,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        vec![1, 41, 30, 255, 2, 10, 187, 255],
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    assert_eq!(
        emission_mask(&tied).unwrap().data.unwrap(),
        [1, 41, 30, 255, 0, 0, 0, 255]
    );
}
#[test]
fn real_laser_hits_vehicle_surface_skips_enemy_and_hides_when_unfitted() {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_resource::<Visuals>()
    .init_resource::<ItemCatalog>()
    .init_resource::<ViewmodelState>()
    .insert_resource(Switches {
        tactical_lit: true,
        tactical_id: 151,
        ..default()
    })
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    })
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(
        PostUpdate,
        render.after(bevy::transform::TransformSystems::Propagate),
    );
    app.world_mut().spawn((
        PlayerCamera,
        Transform::from_xyz(0., 1., 0.),
        Projection::Perspective(PerspectiveProjection::default()),
    ));
    app.world_mut()
        .spawn((ViewmodelCamera, Transform::from_xyz(0., 1., 0.)));
    app.world_mut()
        .spawn((RailEmitter, Transform::from_xyz(0.1, 0.94, -0.35)));
    // The original mask is WORLD|VEHICLE|PROP, deliberately excludes enemy bit1.
    app.world_mut().spawn((
        Collider::cuboid(1., 1., 0.1),
        RigidBody::Fixed,
        Transform::from_xyz(0., 1., -5.),
        CollisionGroups::new(Group::GROUP_6, Group::ALL),
    ));
    app.world_mut().spawn((
        Collider::cuboid(1., 1., 0.1),
        RigidBody::Fixed,
        Transform::from_xyz(0., 1., -10.),
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    for _ in 0..4 {
        app.update();
    }
    let visuals = app.world().resource::<Visuals>();
    let beam = visuals.beam.unwrap();
    let dot = visuals.dot.unwrap();
    let pose = app.world().get::<Transform>(dot).unwrap();
    assert!(
        pose.translation
            .abs_diff_eq(Vec3::new(0., 1., -9.85), 0.001),
        "{:?}",
        pose.translation
    );
    let pose = app.world().get::<Transform>(beam).unwrap();
    assert!(
        pose.transform_point(Vec3::NEG_Y * 0.5)
            .abs_diff_eq(Vec3::new(0.1, 0.94, -0.35), 1e-5)
    );
    assert_eq!(
        *app.world().get::<Visibility>(dot).unwrap(),
        Visibility::Visible
    );
    let mut perspective = crate::avatar::Perspective::default();
    perspective.first_person = false;
    app.insert_resource(perspective);
    let body_emitter = Vec3::new(-0.4, 0.7, -1.2);
    app.world_mut()
        .spawn(crate::avatar::AvatarMuzzle(body_emitter));
    app.update();
    let pose = app.world().get::<Transform>(beam).unwrap();
    assert!(
        pose.transform_point(Vec3::NEG_Y * 0.5)
            .abs_diff_eq(body_emitter, 1e-5)
    );
    assert!(
        app.world()
            .get::<Transform>(dot)
            .unwrap()
            .translation
            .abs_diff_eq(Vec3::new(0., 1., -9.85), 0.001)
    );
    app.world_mut().resource_mut::<Switches>().tactical_lit = false;
    app.update();
    for entity in [beam, dot] {
        assert_eq!(
            *app.world().get::<Visibility>(entity).unwrap(),
            Visibility::Hidden
        );
    }
}
#[test]
fn torch_secondary_is_a_switch_and_never_spends_a_strong_melee_hit() {
    let mut trigger = crate::weapons::Trigger::default();
    let actions = trigger.step(
        0.02,
        276,
        &Value::Null,
        &json!({"light":true}),
        &Value::Null,
        crate::weapons::Buttons {
            strong: true,
            ..default()
        },
        true,
    );
    assert!(actions.is_empty());
    let actions = trigger.step(
        0.02,
        276,
        &Value::Null,
        &json!({"light":true}),
        &Value::Null,
        crate::weapons::Buttons {
            press: true,
            ..default()
        },
        true,
    );
    assert_eq!(actions, [crate::weapons::Intent::Melee(false)]);
}
