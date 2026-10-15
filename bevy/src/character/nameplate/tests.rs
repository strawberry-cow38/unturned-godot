use super::*;
use bevy::ecs::system::RunSystemOnce;

#[test]
fn original_font_shapes_plain_unicode_with_transparent_black_outline() {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        bevy::text::TextPlugin,
    ))
    .init_asset::<Image>()
    .init_resource::<raster::OriginalFont>();
    app.update();
    app.update();
    let bitmap = app
        .world_mut()
        .run_system_once(|mut raster: raster::Raster| raster.render("Ålice <img>"))
        .unwrap()
        .expect("original Open Sans must shape without a camera/UI text entity");
    assert!(bitmap.image.width() > 200);
    assert!(bitmap.image.width() < 1000);
    let data = bitmap.image.data.as_ref().unwrap();
    let level0 = &data[..(bitmap.image.width() * bitmap.image.height() * 4) as usize];
    assert!(
        level0.chunks_exact(4).any(|p| p[0] == 255 && p[3] == 255),
        "white glyph interior"
    );
    assert!(
        level0.chunks_exact(4).any(|p| p[0] == 0 && p[3] == 217),
        "original85% black outline"
    );
    assert!(
        level0.chunks_exact(4).any(|p| p[3] == 0),
        "transparent surround"
    );
    assert!(bitmap.image.texture_descriptor.mip_level_count > 1);
    assert!(bitmap.offset.is_finite());
}

#[test]
fn decoded_avatar_has_real_mips_and_bad_nonnull_bytes_use_the_source_checker() {
    let decoded = decode_picture(include_bytes!("tests/avatar.png"), 128);
    assert_eq!((decoded.width(), decoded.height()), (128, 128));
    assert_eq!(decoded.texture_descriptor.mip_level_count, 8);
    let broken = decode_picture(b"not a PNG", 128);
    let pixels = broken.data.as_ref().unwrap();
    assert_eq!(&pixels[..4], &[255, 0, 255, 255]);
    assert_eq!(&pixels[16 * 4..17 * 4], &[13, 13, 13, 255]);
    assert_eq!(&pixels[16 * 128 * 4..16 * 128 * 4 + 4], &[13, 13, 13, 255]);
    assert_ne!(decoded.data, broken.data);
    let wrong_size = decode_picture(include_bytes!("tests/avatar.png"), 64);
    assert_eq!((wrong_size.width(), wrong_size.height()), (64, 64));
    assert_eq!(&wrong_size.data.as_ref().unwrap()[..4], &[255, 0, 255, 255]);
}

#[test]
fn source_range_uses_plate_position_and_gear_height_never_lowers_the_anchor() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins).add_systems(Update, range);
    let plate = app
        .world_mut()
        .spawn((
            Nameplate {
                name: "Name".into(),
                picture: None,
            },
            GlobalTransform::from_translation(Vec3::Y * 2.05),
        ))
        .id();
    let camera = app
        .world_mut()
        .spawn((
            crate::player::PlayerCamera,
            GlobalTransform::from_translation(Vec3::new(60., 2.05, 0.)),
        ))
        .id();
    app.update();
    assert_eq!(
        *app.world().get::<Visibility>(plate).unwrap(),
        Visibility::Inherited
    );
    app.world_mut().entity_mut(plate).insert(NameplateEnabled(false));
    app.update();
    assert_eq!(*app.world().get::<Visibility>(plate).unwrap(), Visibility::Hidden);
    app.world_mut().get_mut::<NameplateEnabled>(plate).unwrap().0 = true;
    app.update();
    assert_eq!(*app.world().get::<Visibility>(plate).unwrap(), Visibility::Inherited);
    *app.world_mut().get_mut::<GlobalTransform>(camera).unwrap() =
        GlobalTransform::from_translation(Vec3::new(60.01, 2.05, 0.));
    app.update();
    assert_eq!(
        *app.world().get::<Visibility>(plate).unwrap(),
        Visibility::Hidden
    );
    assert_eq!(Nameplate::above_gear(1.5), Nameplate::HEIGHT);
    assert!((Nameplate::above_gear(2.4) - 2.55).abs() < 1e-5);
}
