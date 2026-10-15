use super::*;
use crate::{vehicle::data, world::WorldConfig};

#[test]
fn source_convex_band_keeps_boundary_vertices_and_midpoint_even_planes() {
    let positions = [-0.0015, 0.0025]
        .into_iter()
        .flat_map(|y| {
            [
                [-1., y, -1.],
                [1., y, -1.],
                [1., y, 1.],
                [-1., y, 1.],
                [0., y, 0.],
            ]
        })
        .collect();
    let source = ObjData {
        positions,
        ..default()
    };
    let points = band_points(&source, Vec3::splat(-1.), Vec3::ONE).unwrap();
    assert_eq!(points.len(), 8);
    assert!(points[..4].iter().all(|p| p.y == -0.002));
    assert!(points[4..].iter().all(|p| p.y == 0.002));
    assert!(band_points(&source, Vec3::new(-1., 0., -1.), Vec3::ONE).is_none());
}

#[test]
fn shipping_authored_hulls_replace_the_fallback_and_all_parked_supports_survive() {
    let root = WorldConfig::default().content_root;
    assert!(
        data::FLEET.iter().all(|d| d.spec.hull_decompose.is_none()),
        "a new region-decomposition spec requires its own exact source branch"
    );
    assert_eq!(
        data::FLEET
            .iter()
            .filter(|d| d.spec.landing_gear_size != [0.; 3])
            .count(),
        7
    );
    for name in [
        "ship",
        "dinky_trailer",
        "small_trailer",
        "medium_trailer",
        "large_trailer",
        "horsebox_trailer",
        "animal_trailer",
    ] {
        let d = data::named(name).unwrap();
        assert!(d.spec.hull_decompose.is_none());
        let source = ObjData::load_source_faces(&root.join(d.spec.body.as_ref().unwrap())).unwrap();
        let (shapes, pending) = initial(d, Some(&source), name == "ship", true, false);
        assert!(!pending);
        let bands = usize::from(name == "ship");
        let gear = usize::from(d.spec.landing_gear_size != [0.; 3]);
        assert_eq!(
            shapes.len(),
            bands
                + d.spec.hull_boxes.as_ref().unwrap().len()
                + gear
                + d.spec.extra_boxes.as_ref().map_or(0, Vec::len)
                + usize::from(d.roof.is_some()),
            "{name}: no retained broad BellyBox"
        );
        if name == "ship" {
            assert!(
                !shapes
                    .iter()
                    .any(|(p, _, c)| *p == Vec3::from_array(d.spec.box_center)
                        && c.as_cuboid().is_some_and(
                            |b| b.half_extents() * 2. == Vec3::from_array(d.spec.box_size)
                        )),
                "{name}"
            );
        }
        if gear > 0 {
            let (p, r, c) = shapes.last().unwrap();
            assert_eq!(*p, Vec3::from_array(d.spec.landing_gear_center));
            assert_eq!(*r, Quat::IDENTITY);
            assert_eq!(
                c.as_cuboid().unwrap().half_extents() * 2.,
                Vec3::from_array(d.spec.landing_gear_size)
            );
        }
    }
    let d = data::named("trailer").unwrap();
    let source = ObjData::load_source_faces(&root.join(d.spec.body.as_ref().unwrap())).unwrap();
    let (shapes, pending) = initial(d, Some(&source), false, false, false);
    assert!(pending);
    assert_eq!(
        shapes.last().unwrap().0,
        Vec3::from_array(d.spec.landing_gear_center)
    );
    // The shipping trailer currently has no committed VHACD cache. Feed the
    // replacement seam a convex from its actual body, as the async path does;
    // this test is about retaining support through replacement, not decomposition.
    let points = source
        .positions
        .iter()
        .copied()
        .map(Vec3::from_array)
        .collect::<Vec<_>>();
    let baked = super::super::with_extras(d, vec![Collider::convex_hull(&points).unwrap()]);
    assert!(
        baked
            .as_compound()
            .unwrap()
            .raw
            .shapes()
            .iter()
            .any(
                |(p, c)| p.translation == Vec3::from_array(d.spec.landing_gear_center)
                    && c.as_cuboid().is_some()
            ),
        "ordinary trailer landing legs must survive asynchronous hull replacement"
    );
}

#[test]
fn empty_authored_regions_fall_back_and_shipbox_keeps_the_source_car_branch() {
    let mut d = data::named("ship").unwrap().clone();
    let source = ObjData::load_source_faces(
        &WorldConfig::default()
            .content_root
            .join(d.spec.body.as_ref().unwrap()),
    )
    .unwrap();
    d.spec.hull_bands = Some(Vec::new());
    d.spec.hull_boxes = Some(Vec::new());
    let (empty, pending) = initial(&d, Some(&source), false, true, false);
    assert!(!pending);
    assert_eq!(empty.len(), 1);
    assert_eq!(
        empty[0].2.as_cuboid().unwrap().half_extents() * 2.,
        Vec3::from_array(d.spec.box_size)
    );
    assert!(
        initial(&d, Some(&source), true, true, false).0.is_empty(),
        "an independent source HullMesh already counts as made"
    );
    assert!(
        initial(&d, Some(&source), false, false, false).1,
        "UG_SHIPBOX bypasses authored geometry, then reaches normal car decomposition"
    );
    assert!(
        !initial(&d, Some(&source), false, false, true).1,
        "UG_BOXHULL also suppresses that car bake"
    );
}

#[test]
fn actual_ship_builder_blocks_the_broad_side_but_preserves_the_tapered_bow() {
    use bevy_rapier3d::prelude::*;
    let mut app = super::super::hull_mesh_tests::app();
    let (owner, _) = super::super::hull_mesh_tests::ship(&mut app);
    let shape = app.world().get::<Collider>(owner).unwrap();
    assert_eq!(
        shape.as_compound().unwrap().raw.shapes().len(),
        10,
        "band + nine source boxes, without old full-width box"
    );
    let beyond_old_box = Vec3::new(11., 10., 0.);
    let outside_bow = Vec3::new(8., 5., -30.);
    assert!(shape.contains_point(Vec3::ZERO, Quat::IDENTITY, beyond_old_box));
    assert!(!shape.contains_point(Vec3::ZERO, Quat::IDENTITY, outside_bow));
    let old = Collider::cuboid(10., 5.5, 33.);
    assert!(!old.contains_point(Vec3::Y * 5.5, Quat::IDENTITY, beyond_old_box));
    assert!(old.contains_point(Vec3::Y * 5.5, Quat::IDENTITY, outside_bow));
    let side = app
        .world_mut()
        .spawn((
            RigidBody::Dynamic,
            Collider::ball(0.2),
            Transform::from_xyz(14., 10., 0.),
            GravityScale(0.),
            Velocity::linear(Vec3::NEG_X * 4.),
            Sleeping::disabled(),
            Ccd::enabled(),
        ))
        .id();
    let bow = app
        .world_mut()
        .spawn((
            RigidBody::Dynamic,
            Collider::ball(0.1),
            Transform::from_translation(outside_bow),
            GravityScale(0.),
            Velocity::default(),
            Sleeping::disabled(),
        ))
        .id();
    for _ in 0..90 {
        app.update();
    }
    let at = app.world().get::<Transform>(side).unwrap().translation;
    assert!(
        at.x > 11. && at.x < 13.,
        "real side contact uses reduced source band: {at}"
    );
    assert!(
        app.world()
            .get::<Transform>(bow)
            .unwrap()
            .translation
            .distance(outside_bow)
            < 0.001,
        "no invented broad bow volume pushes the visitor"
    );
    // Reinstate ONLY the old broad box; the same visitor now receives an impulse.
    let mut shapes: Vec<_> = app
        .world()
        .get::<Collider>(owner)
        .unwrap()
        .as_compound()
        .unwrap()
        .raw
        .shapes()
        .iter()
        .map(|(p, c)| (p.translation, p.rotation, Collider::from(c.clone())))
        .collect();
    shapes.push((Vec3::Y * 5.5, Quat::IDENTITY, old));
    app.world_mut()
        .entity_mut(owner)
        .insert(Collider::compound(shapes));
    for _ in 0..30 {
        app.update();
    }
    assert!(
        app.world()
            .get::<Transform>(bow)
            .unwrap()
            .translation
            .distance(outside_bow)
            > 0.1,
        "old retained fallback reproduces the false contact"
    );
}

#[test]
fn original_parked_support_holds_the_trailer_after_hull_replacement() {
    use bevy::ecs::system::RunSystemOnce;
    use bevy_rapier3d::prelude::*;
    let settle = |remove_support: bool| {
        let mut app = super::super::hull_mesh_tests::app();
        let d = data::named("small_trailer").unwrap();
        let owner = crate::vehicle::tests::body(
            &mut app,
            "small_trailer",
            Vec3::Y * (d.spec.ground_clearance() + 0.2),
        );
        app.world_mut()
            .entity_mut(owner)
            .remove::<RayCastVehicleController>();
        app.world_mut()
            .entity_mut(owner)
            .insert((LockedAxes::ROTATION_LOCKED, Sleeping::disabled()));
        app.world_mut()
            .run_system_once(
                move |mut commands: Commands,
                      mut cache: ResMut<ContentCache>,
                      mut meshes: ResMut<Assets<Mesh>>,
                      mut materials: ResMut<Assets<StandardMaterial>>,
                      mut images: ResMut<Assets<Image>>,
                      assets: Res<AssetServer>| {
                    crate::vehicle::render::build(
                        &mut commands,
                        owner,
                        d,
                        0,
                        &WorldConfig::default().content_root,
                        &mut cache,
                        &mut meshes,
                        &mut materials,
                        &mut images,
                        &assets,
                    );
                },
            )
            .unwrap();
        if remove_support {
            let parts = app
                .world()
                .get::<Collider>(owner)
                .unwrap()
                .as_compound()
                .unwrap()
                .raw
                .shapes()
                .iter()
                .filter(|(p, _)| p.translation != Vec3::from_array(d.spec.landing_gear_center))
                .map(|(p, c)| (p.translation, p.rotation, Collider::from(c.clone())))
                .collect();
            app.world_mut()
                .entity_mut(owner)
                .insert(Collider::compound(parts));
        }
        app.world_mut().spawn((
            RigidBody::Fixed,
            Collider::cuboid(10., 0.1, 10.),
            Transform::from_xyz(0., -0.1, 0.),
        ));
        for _ in 0..120 {
            app.update();
        }
        app.world().get::<Transform>(owner).unwrap().translation.y
    };
    let parked = settle(false);
    let expected = data::named("small_trailer")
        .unwrap()
        .spec
        .ground_clearance();
    assert!(
        (parked - expected).abs() < 0.02,
        "source landing support seated on ground: {parked} vs {expected}"
    );
    let missing = settle(true);
    assert!(
        parked - missing > 0.2,
        "removing only the support reproduces the parked chassis drop: {parked} vs {missing}"
    );
}

#[test]
#[ignore = "Generate opt-in original Godot oracle with bevy/tools/vehicle_hull_reference.py"]
fn original_godot_convex_band_matches_every_reduced_point() {
    let path = std::env::var("UG_HULL_ORACLE").expect("path to generated source JSON");
    let rows: serde_json::Value = serde_json::from_slice(&std::fs::read(path).unwrap()).unwrap();
    let root = WorldConfig::default().content_root;
    for row in rows.as_array().unwrap() {
        let name = row["name"].as_str().unwrap();
        let raw: Vec<[f32; 3]> = serde_json::from_value(row["vertices"].clone()).unwrap();
        let faces: Vec<[f32; 3]> = serde_json::from_value(row["faces"].clone()).unwrap();
        assert_eq!(raw.len(), faces.len());
        for (a, b) in raw.iter().zip(&faces) {
            assert_eq!(
                source_face_vertex(*a).to_array().map(f32::to_bits),
                b.map(f32::to_bits),
                "{name}: exact float-step/double-arithmetic snap of {a:?}"
            );
        }
        if name == "snap" {
            continue;
        }
        let source = if name == "ship" {
            ObjData::load_source_faces(
                &root.join(data::named(name).unwrap().spec.body.as_ref().unwrap()),
            )
            .unwrap()
        } else {
            ObjData {
                positions: raw,
                ..default()
            }
        };
        if name == "ship" || name == "region_boundary" {
            let (min, max) = if name == "ship" {
                data::named(name).unwrap().spec.hull_trimesh.unwrap()
            } else {
                ([0., -1., -1.], [1., 1., 1.])
            };
            let min = Vec3::from_array(min);
            let max = Vec3::from_array(max);
            let mut selected: Vec<_> = source
                .positions
                .chunks_exact(3)
                .filter_map(|tri| {
                    let p = [
                        source_face_vertex(tri[0]),
                        source_face_vertex(tri[1]),
                        source_face_vertex(tri[2]),
                    ];
                    p.iter()
                        .all(|v| v.cmpge(min).all() && v.cmple(max).all())
                        .then_some(p)
                })
                .flatten()
                .map(|p| p.to_array())
                .collect();
            let mut expected: Vec<[f32; 3]> =
                serde_json::from_value(row["region"].clone()).unwrap();
            let order = |a: &[f32; 3], b: &[f32; 3]| {
                a[0].total_cmp(&b[0])
                    .then(a[1].total_cmp(&b[1]))
                    .then(a[2].total_cmp(&b[2]))
            };
            selected.sort_by(order);
            expected.sort_by(order);
            assert_eq!(
                selected, expected,
                "{name}: exact source all-corners region after GetFaces"
            );
            if name == "region_boundary" {
                assert_eq!(selected.len(), 3);
                continue;
            }
        }
        let (min, max) = if name == "ship" {
            data::named(name).unwrap().spec.hull_bands.as_ref().unwrap()[0]
        } else {
            ([-1.; 3], [1.; 3])
        };
        let actual = band_points(&source, min.into(), max.into()).unwrap();
        let expected: Vec<[f32; 3]> = serde_json::from_value(row["points"].clone()).unwrap();
        assert_eq!(actual.len(), expected.len(), "{name}");
        for (a, b) in actual.iter().zip(expected) {
            assert_eq!(
                a.to_array().map(f32::to_bits),
                b.map(f32::to_bits),
                "{name}: {a} vs {b:?}"
            );
        }
    }
}

#[test]
fn get_faces_uses_engine_snap_before_the_band_and_region_bounds() {
    assert_eq!(
        source_face_vertex([-0.00005, 0.00005, 9.999994]),
        Vec3::new(0., 0.0001, 10.)
    );
    let d = data::named("ship").unwrap();
    let source = ObjData::load_source_faces(
        &WorldConfig::default()
            .content_root
            .join(d.spec.body.as_ref().unwrap()),
    )
    .unwrap();
    let (min, max) = d.spec.hull_bands.as_ref().unwrap()[0];
    assert_eq!(
        band_points(&source, min.into(), max.into()).unwrap().len(),
        34,
        "raw OBJ micro-offsets incorrectly retain40 points; source GetFaces snaps before reduction"
    );
}
