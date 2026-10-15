use super::*;
#[derive(Resource)]
struct Fixture(api::Tile);
fn draw(mut commands: Commands, fixture: Res<Fixture>, mut images: ResMut<Assets<Image>>) {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
    let mut art = Art::default();
    commands.spawn(Node::default()).with_children(|p| {
        tile(
            p,
            &fixture.0,
            Rect::new(10., 20., 154., 92.),
            0,
            false,
            true,
            true,
            &mut art,
            &root,
            &mut images,
            &Handle::default(),
        );
    });
}
#[test]
fn accepted_tile_preserves_amount_quality_and_draws_fluid_badge_with_clamped_extents() {
    let facts = api::Tile {
        id: 1200,
        name: "Bottle".into(),
        width: 2,
        height: 1,
        amount: 5,
        quality: 37,
        rarity_color: vec![0.2, 0.6, 0.9, 1.],
        quality_color: vec![0.8, 0.4, 0.2, 1.],
        food: true,
        active_auto_drink: true,
        fill: Some(api::Fill {
            amount: 125.,
            capacity: 100.,
            color: vec![0.2, 0.5, 0.9, 1.],
            fuel: false,
        }),
        ..default()
    };
    let mut app = App::new();
    app.init_resource::<Assets<Image>>()
        .insert_resource(Fixture(facts.clone()))
        .add_systems(Update, draw);
    app.update();
    assert_eq!(
        app.world().resource::<Fixture>().0,
        facts,
        "drawing cannot normalize accepted values"
    );
    let world = app.world_mut();
    let (node, bg) = world
        .query_filtered::<(&Node, &BackgroundColor), With<ItemTile>>()
        .single(world)
        .unwrap();
    assert_eq!(
        (node.width, node.height, node.border),
        (px(144.), px(72.), UiRect::ZERO)
    );
    let color = bg.0.to_srgba();
    assert!((color.blue - 0.9 * 0.22).abs() < 1e-6 && (color.alpha - 0.97).abs() < 1e-6);
    let (fill, node) = world.query::<(&FillBar, &Node)>().single(world).unwrap();
    assert_eq!(fill.0, 1.);
    assert_eq!(node.width, px(138.));
    assert_eq!(
        world
            .query_filtered::<Entity, With<AutoDrink>>()
            .iter(world)
            .count(),
        1
    );
    assert_eq!(
        world
            .query_filtered::<Entity, With<ConditionChip>>()
            .iter(world)
            .count(),
        1
    );
    let text: Vec<_> = world
        .query::<&Text>()
        .iter(world)
        .map(|s| s.0.clone())
        .collect();
    assert!(text.iter().any(|s| s == "37%") && text.iter().any(|s| s == "x5"));
}
#[test]
fn preserved_badge_uses_source_centered_six_arm_pixel_art() {
    let image = snowflake();
    let bytes = image.data.as_ref().unwrap();
    assert_eq!((image.width(), image.height()), (40, 40));
    let pixels: Vec<_> = bytes
        .chunks_exact(4)
        .enumerate()
        .filter(|(_, p)| p[3] > 0)
        .collect();
    assert!(pixels.len() > 170 && pixels.len() < 400);
    assert!(pixels.iter().all(|(_, p)| *p == [250, 250, 250, 255]));
    let bounds = pixels
        .iter()
        .fold((UVec2::splat(40), UVec2::ZERO), |(lo, hi), (i, _)| {
            let p = UVec2::new(*i as u32 % 40, *i as u32 / 40);
            (lo.min(p), hi.max(p))
        });
    assert_eq!(bounds, (UVec2::new(6, 7), UVec2::new(33, 33)));
    // No background paint: preserving food never replaces the item image.
    assert_eq!(bytes[3], 0);
}
