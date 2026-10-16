use super::*;
use crate::world::buildings::openings::{self, DoorLeaf, Glass};

fn text(row: &serde_json::Value) -> String {
    let p = &row["position"];
    let r = &row["euler"];
    format!(
        "wall {} {} {} {} 8 {} 0 5 {} Wall\n  open {} {} {} {} 999 0 0 0 0 0 0 {} 1 swing {} {} action {}\n",
        p[0],
        p[1],
        p[2],
        r[1],
        row["thickness"],
        r[0],
        row["u"],
        row["v"],
        row["width"],
        row["height"],
        row["prop"].as_str().unwrap(),
        if row["hinge"].as_bool().unwrap() {
            "start"
        } else {
            "end"
        },
        if row["front"].as_bool().unwrap() {
            "front"
        } else {
            "back"
        },
        match row["action"].as_i64().unwrap() {
            1 => "double",
            2 => "pocket",
            _ => "swing",
        }
    )
}
fn assert_pose(actual: Transform, expected: &serde_json::Value) {
    let v = |x: &serde_json::Value| {
        Vec3::from_array(serde_json::from_value::<[f32; 3]>(x.clone()).unwrap())
    };
    assert!(
        (actual.translation - v(&expected["position"])).length() < 0.0008,
        "{:?} != {:?}",
        actual.translation,
        expected["position"]
    );
    let b = &expected["basis"];
    let expected = Mat3::from_cols(v(&b[0]), v(&b[1]), v(&b[2]));
    assert!(
        (Mat3::from_quat(actual.rotation) - expected)
            .to_cols_array()
            .iter()
            .all(|f| f.abs() < 3e-6)
    );
}
#[test]
fn fitted_leaf_mesh_and_real_collider_follow_original_godot_pivot_pocket_and_double_swing() {
    let rows: serde_json::Value = serde_json::from_str(include_str!(
        "../../../../../tests/UnturnedBevy.Tests/fixtures/building-openings-godot.json"
    ))
    .unwrap();
    let mut count = 0;
    for row in rows.as_array().unwrap() {
        let view = source(json!({"text":text(&row["input"])}));
        let mut app = app();
        replace(app.world_mut(), &view).unwrap();
        app.update();
        app.update();
        let mut leaves: Vec<_> = app
            .world_mut()
            .query::<(Entity, &DoorLeaf)>()
            .iter(app.world())
            .map(|(e, _)| e)
            .collect();
        leaves.sort_by_key(|e| e.index());
        let expected = row["leaves"].as_array().unwrap();
        assert_eq!(leaves.len(), expected.len());
        for n in 0..4 {
            let t = if n == 0 {
                0.
            } else if n == 1 {
                0.5
            } else {
                1.
            };
            let reverse = n == 3;
            if reverse && row["input"]["action"] != 1 {
                continue;
            }
            openings::preview_door(app.world_mut(), 0, 0, t, true, reverse).unwrap();
            app.update();
            app.update();
            for (entity, leaf) in leaves.iter().zip(expected) {
                let mesh = app
                    .world()
                    .get::<GlobalTransform>(*entity)
                    .unwrap()
                    .compute_transform();
                assert_pose(mesh, &leaf["poses"][n]["mesh"]);
                let definition = &app.world().get::<DoorLeaf>(*entity).unwrap().definition;
                let collider = mesh.mul_transform(Transform::from_translation(
                    definition.collider_center.into(),
                ));
                assert_pose(collider, &leaf["poses"][n]["collider"]);
                assert_eq!(
                    app.world().get::<ColliderDisabled>(*entity).is_some(),
                    n == 1
                );
                if n != 1 {
                    let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
                    let context = state.get(app.world()).unwrap();
                    let only =
                        |e: Entity, _: &bevy_rapier3d::rapier::geometry::Collider| e == *entity;
                    assert!(
                        context
                            .single()
                            .unwrap()
                            .cast_ray(
                                collider.translation + collider.rotation * Vec3::Y * 3.,
                                collider.rotation * Vec3::NEG_Y,
                                6.,
                                true,
                                QueryFilter::default().predicate(&only)
                            )
                            .is_some(),
                        "physical collider follows leaf"
                    );
                }
                count += 1;
            }
        }
    }
    assert!(count > 55);
}
#[test]
fn original_glass_layers_material_and_saved_broken_lifetime_remain_distinct_from_wall() {
    let text = "wall 0 0 0 0 6 .5 0 4 0 Wall\n  open 2 1 2 2 999 0 1 4219008 .5 1 0\n";
    let view = source(json!({"text":text}));
    let mut app = app();
    let root = replace(app.world_mut(), &view).unwrap();
    app.update();
    app.update();
    let pane = app
        .world_mut()
        .query_filtered::<Entity, With<Glass>>()
        .single(app.world())
        .unwrap();
    let groups = app.world().get::<CollisionGroups>(pane).unwrap();
    assert!(groups.memberships.contains(SOURCE_PROP));
    assert_eq!(groups.filters, Group::GROUP_2);
    assert_eq!(
        ray(
            &mut app,
            Vec3::new(3., 2., 2.),
            CollisionGroups::new(Group::GROUP_2, Group::GROUP_4)
        ),
        Some(pane)
    );
    assert!(
        ray(
            &mut app,
            Vec3::new(3., 2., 2.),
            CollisionGroups::new(Group::ALL, Group::GROUP_1)
        )
        .is_none(),
        "source world-bit0 LOS ignores glass"
    );
    let material = app
        .world()
        .get::<MeshMaterial3d<crate::vehicle::glazing::rain::GlassMaterial>>(pane)
        .unwrap();
    let m = app
        .world()
        .resource::<Assets<crate::vehicle::glazing::rain::GlassMaterial>>()
        .get(&material.0)
        .unwrap();
    assert_eq!(m.uniform.surface.x, 0.);
    assert_eq!(m.uniform.surface.y, 0.06);
    assert_eq!(m.uniform.weather.z, 1.);
    assert_eq!(m.uniform.tint.w, 0.26);
    let broken = source(json!({"text":text.replace(".5 1 0", ".5 1 1")}));
    replace(app.world_mut(), &broken).unwrap();
    app.update();
    app.update();
    assert!(app.world().get_entity(root).is_err());
    assert!(app.world().get_entity(pane).is_err());
    assert!(
        ray(
            &mut app,
            Vec3::new(3., 2., 2.),
            CollisionGroups::new(Group::GROUP_2, Group::GROUP_4)
        )
        .is_none()
    );
    let mut invalid = view.clone();
    invalid.walls[0].glass_models[0].size[0] = f32::NAN;
    assert!(replace(app.world_mut(), &invalid).is_err());
}

#[test]
fn actual_source_tiny_positive_pane_retains_zero_axis_fallback_without_rejecting_the_stage() {
    let view = source(
        json!({"text":"wall 0 0 0 0 6 .7 0 4 0 Wall\n open 2 1 .00001 .00001 999 0 1 0 1 0 0\n"}),
    );
    assert_eq!(view.walls[0].glass_models[0].axis, [0.; 3]);
    let mut app = app();
    replace(app.world_mut(), &view).unwrap();
    app.update();
    let pane = app
        .world_mut()
        .query_filtered::<Entity, With<Glass>>()
        .single(app.world())
        .unwrap();
    let handle = app
        .world()
        .get::<MeshMaterial3d<crate::vehicle::glazing::rain::GlassMaterial>>(pane)
        .unwrap();
    assert_eq!(
        app.world()
            .resource::<Assets<crate::vehicle::glazing::rain::GlassMaterial>>()
            .get(&handle.0)
            .unwrap()
            .uniform
            .axis,
        Vec4::ZERO
    );
    let mut invalid = view.clone();
    invalid.walls[0].glass_models[0].axis = [0.5, 0., 0.];
    assert!(replace(app.world_mut(), &invalid).is_err());
    assert!(app.world().get_entity(pane).is_ok());
}
