use super::*;
#[test]
fn alpha_corner_rectification_keeps_note_print_and_transparent_input() {
    let empty = vec![0; 4 * 8 * 8];
    assert_eq!(upright(8, 8, &empty), (8, 8, empty));
    let mut pixels = vec![0; 4 * 8 * 8];
    // Independent axis-aligned source art, with a low-alpha fringe excluded.
    for y in 2..5 {
        for x in 1..7 {
            let i = (y * 8 + x) * 4;
            pixels[i..i + 4].copy_from_slice(&[x as u8, y as u8, 80, 255]);
        }
    }
    pixels[3] = 5;
    let (w, h, bytes) = upright(8, 8, &pixels);
    assert_eq!((w, h), (6, 3));
    assert_eq!(&bytes[..4], &[1, 2, 80, 255]);
    assert_eq!(&bytes[bytes.len() - 4..], &[6, 4, 80, 255]);
}
#[test]
fn real_currency_keeps_note_scale_while_spending_and_coins_in_fixed_foreground() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
    let mut images = Assets::<Image>::default();
    let mut art = Art::default();
    let wallet: Vec<_> = [
        (1055, 100),
        (1054, 50),
        (1053, 20),
        (1052, 10),
        (1051, 5),
        (1057, 2),
        (1056, 1),
    ]
    .into_iter()
    .map(|(id, value)| Denomination {
        id,
        value,
        icon: format!("items/icons/{id}.png"),
    })
    .collect();
    let full = fan(&wallet, Vec2::splat(144.), &mut art, &root, &mut images);
    let spent = fan(
        &wallet[2..],
        Vec2::splat(144.),
        &mut art,
        &root,
        &mut images,
    );
    assert_eq!(
        full.iter().map(|p| p.id).collect::<Vec<_>>(),
        vec![1055, 1054, 1053, 1052, 1051, 1057, 1056]
    );
    assert_eq!(full[2].size, spent[0].size);
    assert_eq!(full[5].center, spent[3].center);
    assert_eq!(full[6].center, spent[4].center);
    assert!((full[0].radians.to_degrees() + 124.).abs() < 1e-4);
    assert!((full[4].radians.to_degrees() + 56.).abs() < 1e-4);
    for p in full {
        for corner in [
            Vec2::new(-0.5, -0.5),
            Vec2::new(-0.5, 0.5),
            Vec2::new(0.5, -0.5),
            Vec2::new(0.5, 0.5),
        ] {
            let point = p.center + Rot2::radians(p.radians) * (corner * p.size);
            assert!(point.cmpge(Vec2::ZERO).all() && point.cmple(Vec2::splat(144.)).all());
        }
    }
}
