use super::*;

fn triangle() -> Set {
    Set {
        triangles: vec![[
            Vertex {
                position: Vec3::ZERO,
                normal: Vec3::Z,
                uv: Vec2::ZERO,
                color: Vec4::ZERO,
            },
            Vertex {
                position: Vec3::X * 2.,
                normal: Vec3::Z,
                uv: Vec2::X,
                color: Vec4::ONE,
            },
            Vertex {
                position: Vec3::Y * 2.,
                normal: Vec3::Z,
                uv: Vec2::Y,
                color: Vec4::ONE,
            },
        ]],
    }
}
fn area(set: &Set) -> f32 {
    set.triangles
        .iter()
        .map(|v| {
            (v[1].position - v[0].position)
                .cross(v[2].position - v[0].position)
                .length()
                * 0.5
        })
        .sum()
}
#[test]
fn plane_split_preserves_area_winding_and_interpolated_authored_attributes() {
    let original = triangle();
    let (below, above, cut) = original.split(0, 1.);
    assert_eq!(below.triangles.len(), 2);
    assert_eq!(above.triangles.len(), 1);
    assert!((area(&below) + area(&above) - area(&original)).abs() < 1e-6);
    assert_eq!(cut.len(), 2, "crossings recorded once, not once per half");
    assert_eq!(cut[0].position, Vec3::X);
    assert_eq!(cut[0].uv, Vec2::new(0.5, 0.));
    assert_eq!(cut[0].color, Vec4::splat(0.5));
    assert!(below.triangles.iter().chain(&above.triangles).all(|v| {
        (v[1].position - v[0].position)
            .cross(v[2].position - v[0].position)
            .z
            > 0.
    }));
    let data = below.commit();
    assert_eq!(data.min, Vec3::ZERO);
    assert_eq!(data.max, Vec3::new(1., 2., 0.));
    assert_eq!(data.positions.len(), data.uvs.len());
    assert_eq!(data.positions.len(), data.colors.len());
}
#[test]
fn box_subtraction_clips_crossing_faces_and_cap_uses_source_outer_loop() {
    let original = triangle();
    let (outside, removed) = original.subtract_box(Vec3::new(0.5, 0.5, -1.), Vec3::new(1., 1., 1.));
    assert!((area(&outside) - 1.75).abs() < 1e-6);
    assert!(!removed.is_empty());
    let mut capped = Set::default();
    let sample = triangle().triangles[0][0];
    let cut: Vec<_> = [
        Vec3::ZERO,
        Vec3::X,
        Vec3::ONE * 0.0001,
        Vec3::Y,
        Vec3::X + Vec3::Y,
        Vec3::new(0.5, 0.5, 0.),
    ]
    .into_iter()
    .map(|position| Vertex { position, ..sample })
    .collect();
    capped.cap_hull(&cut, 2, -Vec3::Z, Vec2::splat(0.25), Vec4::splat(0.7));
    assert_eq!(
        capped.triangles.len(),
        2,
        "interior/near-duplicate points do not add cap faces"
    );
    assert!((area(&capped) - 1.).abs() < 1e-6);
    for v in &capped.triangles {
        assert!(
            (v[1].position - v[0].position)
                .cross(v[2].position - v[0].position)
                .z
                < 0.,
            "generated normal defines Bevy front face"
        );
        assert!(v.iter().all(|q| q.uv == Vec2::splat(0.25)
            && q.color == Vec4::splat(0.7)
            && q.normal == -Vec3::Z));
    }
}
