use super::*;
#[test]
fn retail_nodes_keep_engine_z_and_all_medium_maps_use_1920_metres() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
    for name in ["nodes.tsv", "nodes_Washington.tsv", "nodes_Yukon.tsv"] {
        let nodes = assets::parse_nodes(&std::fs::read_to_string(root.join(name)).unwrap());
        assert_eq!(
            nodes.len(),
            match name {
                "nodes.tsv" => 21,
                "nodes_Washington.tsv" => 16,
                _ => 8,
            }
        );
        assert!(
            nodes
                .iter()
                .all(|(_, p)| p.cmpge(Vec2::ZERO).all() && p.cmple(Vec2::ONE).all())
        );
    }
    let nodes = assets::parse_nodes("Airport\t724.35,34.34,-509.01\nBad\tNaN,0,0");
    assert_eq!(nodes.len(), 1);
    assert!((nodes[0].1 - Vec2::new(0.8772656, 0.23489061)).length() < 0.000001);
    assert!(!available("Procedural"));
}
#[test]
fn zoom_anchor_pan_limits_and_marker_hit_are_screen_space() {
    let mut view = geometry::View::default();
    let cursor = Vec2::new(120., 280.);
    let side = 572.;
    let norm = cursor / side;
    view.zoom_at(cursor, 2., side);
    assert!(((cursor - view.pan) / (side * view.zoom) - norm).length() < 0.000001);
    view.marker(cursor, side);
    assert_eq!(view.markers.len(), 1);
    view.zoom_at(cursor, 2., side);
    assert_eq!(view.markers[0].norm, norm);
    view.marker(cursor + Vec2::new(13., 0.), side);
    assert!(view.markers.is_empty());
    view.marker(cursor, side);
    assert_eq!(view.markers[0].serial, 2);
    view.pan = Vec2::new(99999., -99999.);
    view.clamp(side);
    assert_eq!(view.pan, Vec2::new(0., side - side * 4.));
    view.zoom_at(cursor, 0.001, side);
    assert_eq!(view.zoom, 1.);
    assert_eq!(view.pan, Vec2::ZERO);
}
#[test]
fn source_tile_window_is_bounded_and_frees_every_tile_below_two_x() {
    let mut view = geometry::View::default();
    assert!(!(0..16).any(|i| view.tile_visible(i / 4, i % 4, 572.)));
    view.zoom_at(Vec2::splat(286.), 8., 572.);
    let count = (0..16)
        .filter(|i| view.tile_visible(i / 4, i % 4, 572.))
        .count();
    assert_eq!(count, 4);
    view.pan = Vec2::ZERO;
    assert_eq!(
        (0..16)
            .filter(|i| view.tile_visible(i / 4, i % 4, 572.))
            .count(),
        1
    );
    view.zoom = 1.99;
    assert!(!(0..16).any(|i| view.tile_visible(i / 4, i % 4, 572.)));
}
#[test]
fn labels_flip_at_edge_and_lower_priority_collisions_do_not_block_others() {
    let window = Rect::from_corners(Vec2::ZERO, Vec2::splat(500.));
    let mut placed = vec![];
    let first = geometry::label(
        Vec2::new(480., 250.),
        Vec2::new(100., 24.),
        20.,
        window,
        &mut placed,
    )
    .unwrap();
    assert_eq!(first, Vec2::new(366., 238.));
    assert!(
        geometry::label(
            Vec2::new(380., 250.),
            Vec2::new(80., 24.),
            18.,
            window,
            &mut placed
        )
        .is_none()
    );
    assert!(
        geometry::label(
            Vec2::new(-1000., 250.),
            Vec2::new(80., 24.),
            15.,
            window,
            &mut placed
        )
        .is_none()
    );
    assert_eq!(placed.len(), 1);
    assert!(
        geometry::label(
            Vec2::new(100., 100.),
            Vec2::new(80., 24.),
            15.,
            window,
            &mut placed
        )
        .is_some()
    );
}
#[test]
fn source_map_layout_and_camera_facing_are_independent_of_body_heading() {
    let layout = geometry::Layout::new(Vec2::new(1280., 720.));
    assert_eq!(
        layout.map,
        Rect::from_corners(Vec2::new(676., 116.), Vec2::new(1248., 688.))
    );
    assert_eq!(layout.quests.height(), 194.);
    let safe_bottom = super::super::vitals::content_bottom(
        Vec2::new(1280., 720.),
        layout.roster.min.x,
        layout.roster.max.x,
        layout.roster.max.y,
    );
    assert!(
        layout.roster.min.y + 30. + 66. + 18. <= safe_bottom,
        "Two roster rows must remain above the vitals at720p"
    );
    let large = geometry::Layout::new(Vec2::new(1920., 1080.));
    assert_eq!(
        large.quests.height(),
        533.,
        "Original58% split stays unchanged where it fits"
    );
    let rotation = Quat::from_rotation_y(-std::f32::consts::FRAC_PI_2) * Quat::from_rotation_x(0.5);
    assert!((geometry::facing(rotation) - std::f32::consts::FRAC_PI_2).abs() < 0.000001);
}
#[test]
fn dashboard_keys_switch_tabs_close_same_tab_and_do_not_open_a_false_infinite_map() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<UiState>()
        .init_resource::<Settings>()
        .init_resource::<WorldConfig>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<KeyboardInput>()
        .add_message::<UiIntent>()
        .add_systems(Update, keyboard_ui);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    for (key, expected) in [
        (KeyCode::KeyM, Screen::Information),
        (KeyCode::KeyY, Screen::Crafting),
        (KeyCode::Tab, Screen::Inventory),
        (KeyCode::KeyM, Screen::Information),
        (KeyCode::KeyM, Screen::Playing),
    ] {
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .reset_all();
        app.world_mut()
            .resource_mut::<ButtonInput<KeyCode>>()
            .press(key);
        app.update();
        assert_eq!(app.world().resource::<UiState>().screen, expected);
    }
    app.world_mut().resource_mut::<WorldConfig>().map_name = "Procedural".into();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .reset_all();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyM);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
}

#[test]
fn real_dashboard_systems_preserve_view_across_close_and_clear_session_annotations() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Image>()
        .init_asset::<Font>()
        .init_resource::<crate::character::nameplate::OriginalFont>()
        .init_resource::<Dashboard>()
        .init_resource::<MapDiagnostics>()
        .init_resource::<MapAssets>()
        .init_resource::<UiState>()
        .init_resource::<UiModel>()
        .init_resource::<npc::Menu>()
        .init_resource::<Settings>()
        .init_resource::<WorldConfig>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<WorldOrigin>()
        .init_resource::<GameSnapshot>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<MouseWheel>()
        .add_systems(
            Update,
            (sync, input, render::build, render::font, render::place).chain(),
        );
    app.world_mut().spawn((
        Window {
            resolution: (1280, 720).into(),
            ..default()
        },
        PrimaryWindow,
    ));
    app.world_mut().resource_mut::<UiModel>().connected = true;
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"connected":true,"playerId":1,"player":{"profile":{"name":"Accepted Name"}}});
    app.update();
    let world = app.world_mut();
    assert_eq!(
        world
            .query_filtered::<Entity, With<render::Root>>()
            .iter(world)
            .count(),
        1
    );
    let dashboard_root = world
        .query_filtered::<Entity, With<render::Root>>()
        .single(world)
        .unwrap();
    assert!(
        world.get::<crate::ui::backdrop::Backdrop>(dashboard_root).is_some()
    );
    assert_eq!(
        world
            .get::<crate::ui::swoop::Swoop>(dashboard_root)
            .unwrap()
            .0,
        Screen::Information
    );
    // Node requires a transparent BackgroundColor even when the bundle omits it.
    assert_eq!(
        world.get::<BackgroundColor>(dashboard_root).unwrap().0.alpha(),
        0.,
        "source frost replaces the old opaque dark scrim"
    );
    assert!(
        world
            .query::<&Text>()
            .iter(world)
            .any(|t| t.0 == "Accepted Name (you)")
    );
    {
        let mut map = world.resource_mut::<Dashboard>();
        map.view.zoom_at(Vec2::splat(200.), 3., 572.);
        map.view.marker(Vec2::splat(200.), 572.);
    }
    let before = app.world().resource::<Dashboard>().view.clone();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    app.update();
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    app.update();
    let view = &app.world().resource::<Dashboard>().view;
    assert_eq!(view.zoom, before.zoom);
    assert_eq!(view.pan, before.pan);
    assert_eq!(view.markers, before.markers);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    let view = &app.world().resource::<Dashboard>().view;
    assert_eq!(view.zoom, 1.);
    assert!(view.markers.is_empty());
    let old_root = {
        let world = app.world_mut();
        world
            .query_filtered::<Entity, With<render::Root>>()
            .single(world)
            .unwrap()
    };
    app.world_mut().resource_mut::<WorldConfig>().map_name = "Washington".into();
    app.update();
    let world = app.world_mut();
    assert_ne!(
        world
            .query_filtered::<Entity, With<render::Root>>()
            .single(world)
            .unwrap(),
        old_root,
        "Map/content identity must invalidate the overview and town entities even with identical player names and viewport"
    );
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    let world = app.world_mut();
    assert_eq!(
        world
            .query_filtered::<Entity, With<render::Root>>()
            .iter(world)
            .count(),
        0
    );
}

#[test]
fn actual_asset_server_loads_original_overview_and_jpeg_tiles_from_content_root() {
    use bevy::image::{CompressedImageFormats, ImageLoader};
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin {
            file_path: config
                .content_root
                .canonicalize()
                .unwrap()
                .to_string_lossy()
                .into_owned(),
            ..default()
        },
    ))
    .init_asset::<Image>()
    .register_asset_loader(ImageLoader::new(CompressedImageFormats::NONE));
    let server = app.world().resource::<AssetServer>().clone();
    let mut art = MapAssets::default();
    art.load(
        &config,
        &server,
        &mut app.world_mut().resource_mut::<Assets<Image>>(),
    );
    let mut view = geometry::View::default();
    view.zoom = 8.;
    art.tiles(&view, 572., &config, &server);
    let overview = art.overview.clone().unwrap();
    let tile = art.tiles[0].clone().unwrap();
    assert!(server.get_path(overview.id()).unwrap().path().is_relative());
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
    loop {
        app.update();
        let images = app.world().resource::<Assets<Image>>();
        if images.contains(overview.id()) && images.contains(tile.id()) {
            break;
        }
        assert!(
            std::time::Instant::now() < deadline,
            "Original map images must actually decode through the approved asset root"
        );
        std::thread::sleep(std::time::Duration::from_millis(5));
    }
    app.insert_resource(art)
        .add_systems(Update, assets::mipmaps);
    app.update();
    let images = app.world().resource::<Assets<Image>>();
    assert!(images.get(&overview).unwrap().width() >= 1024);
    assert!(
        images
            .get(&tile)
            .unwrap()
            .texture_descriptor
            .mip_level_count
            > 1
    );
    app.world_mut().resource_mut::<MapAssets>().tiles(
        &geometry::View::default(),
        572.,
        &config,
        &server,
    );
    assert!(
        app.world()
            .resource::<MapAssets>()
            .tiles
            .iter()
            .all(Option::is_none)
    );
}

#[test]
fn wheel_events_use_source_direction_step_instead_of_platform_line_or_pixel_magnitude() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_resource::<UiState>()
        .init_resource::<Dashboard>()
        .init_resource::<MapAssets>()
        .init_resource::<WorldConfig>()
        .init_resource::<ButtonInput<MouseButton>>()
        .add_message::<MouseWheel>()
        .add_systems(Update, input);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Information;
    app.world_mut().resource_mut::<Dashboard>().layout =
        Some(geometry::Layout::new(Vec2::new(1280., 720.)));
    let mut window = Window {
        resolution: (1280, 720).into(),
        ..default()
    };
    window.set_cursor_position(Some(Vec2::new(900., 400.)));
    let entity = app.world_mut().spawn((window, PrimaryWindow)).id();
    for (unit, y, expected) in [
        (MouseScrollUnit::Line, 2., 1.18),
        (MouseScrollUnit::Pixel, -20., 1.),
    ] {
        app.world_mut().write_message(MouseWheel {
            unit,
            x: 0.,
            y,
            window: entity,
            phase: bevy::input::touch::TouchPhase::Moved,
        });
        app.update();
        assert!((app.world().resource::<Dashboard>().view.zoom - expected).abs() < 0.00001);
    }
}
