//! Original anti-roll extension against an actual Godot 4.6.2/Jolt VehicleBody3D.
use super::*;

#[test]
fn source_wheel_extension_reads_an_air_gap_from_the_godot_wheel_node_not_compression() {
    let rows: serde_json::Value = serde_json::from_str(include_str!(
        "../../../data/wheel-extension-reference.json"
    ))
    .unwrap();
    // A non-identity body pose: the ray must start at the node under the
    // CURRENT pose and point along that pose's down axis.
    let pose = Transform::from_xyz(12., -3., 40.)
        .with_rotation(Quat::from_euler(EulerRot::YXZ, 0.8, 0.1, -0.05));
    let mut cases = 0;
    for row in rows.as_array().unwrap() {
        for (connection, w) in row["connections"]
            .as_array()
            .unwrap()
            .iter()
            .zip(row["wheels"].as_array().unwrap())
        {
            let v = |value: &serde_json::Value| {
                Vec3::new(
                    value[0].as_f64().unwrap() as f32,
                    value[1].as_f64().unwrap() as f32,
                    value[2].as_f64().unwrap() as f32,
                )
            };
            let (connection, node) = (v(connection), v(&w["local"]));
            let f = |key: &str| w[key].as_f64().unwrap() as f32;
            let mut wheel = VehicleWheel::new(
                connection,
                Vec3::NEG_Y,
                Vec3::X,
                f("rest"),
                f("radius"),
                WheelTuning {
                    max_suspension_travel: f("travel"),
                    ..default()
                },
            );
            // Godot's node sits at hard point + direction x suspension length.
            wheel.state.suspension_length = connection.y - node.y;
            wheel.state.is_in_contact = w["contact"].as_bool().unwrap();
            let ground = w["groundDistance"].as_f64().map(|d| d as f32);
            let read = wheel_extension(
                &wheel,
                |from, reach| {
                    let at = pose.transform_point(node);
                    assert!(from.distance(at) < 1e-4, "{}: ray starts at the posed wheel node", row["case"]);
                    assert!((reach - (f("radius") + f("rest") + f("travel"))).abs() < 1e-6);
                    ground.filter(|d| *d <= reach)
                },
                &pose,
            );
            assert!(
                (read - f("extension")).abs() < 1e-4,
                "{} {connection}: native {read} vs source {}",
                row["case"],
                f("extension")
            );
            cases += 1;
        }
    }
    assert_eq!(cases, 12);
    // The oracle holds every outcome: grounded at any compression, a hanging
    // wheel over ground within reach, and no ground in reach at all.
    let all: Vec<f64> = rows
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|r| r["wheels"].as_array().unwrap().iter())
        .map(|w| w["extension"].as_f64().unwrap())
        .collect();
    assert!(all.iter().any(|e| *e == 0.) && all.iter().any(|e| *e > 0. && *e < 1.));
    assert!(
        rows.as_array().unwrap().iter()
            .flat_map(|r| r["wheels"].as_array().unwrap().iter())
            .any(|w| w["groundDistance"].is_null() && w["extension"] == 1.),
        "no ground within reach reads 1"
    );
}

#[test]
fn bar_is_a_pure_couple_about_body_z_from_left_minus_right_extension() {
    let spec = &super::super::data::named("jeep").unwrap().spec;
    let mut wheels = spec.controller();
    for wheel in &mut wheels.wheels {
        wheel.state.is_in_contact = true;
    }
    let pose = Transform::from_rotation(Quat::from_rotation_y(0.7));
    let left_high = anti_roll(spec, &wheels, &pose, |w| {
        if w.chassis_connection_point.x < 0. { 1. } else { 0. }
    });
    assert!(left_high.length() > 0.);
    assert!(left_high.normalize().dot(pose.rotation * Vec3::Z) > 0.999);
    assert_eq!(anti_roll(spec, &wheels, &pose, |_| 0.), Vec3::ZERO);
    for wheel in &mut wheels.wheels {
        wheel.state.is_in_contact = false;
    }
    assert_eq!(
        anti_roll(spec, &wheels, &pose, |w| w.chassis_connection_point.x.signum()),
        Vec3::ZERO,
        "an axle entirely in the air has nothing to react against"
    );
}

#[test]
fn park_latch_follows_source_set_and_clear_sites_and_exit_does_not_park() {
    let mut park = Park::default();
    assert!(park.parked, "vehicles spawn parked (Vehicle.Build _parked = true)");
    park.update(false, 0, false);
    assert!(park.parked, "nothing touched it");
    park.update(true, 0, false);
    assert!(!park.parked, "Drive() clears _parked");
    park.update(false, 0, false);
    assert!(!park.parked, "the driver's exit does NOT park (NetTests asserts !Parked after exit)");
    let mut trailer = Park::default();
    trailer.update(false, 10, false);
    assert!(!trailer.parked, "CoupleTo wakes the trailer");
    trailer.update(false, 10, false);
    trailer.update(false, 0, false);
    assert!(trailer.parked, "Uncouple re-parks the dropped trailer");
    trailer.update(false, 0, true);
    assert!(!trailer.parked, "OnVehicleContact: a ram wakes a parked vehicle");
    let mut remote = Park::default();
    remote.hold();
    remote.update(false, 0, false);
    assert!(!remote.parked, "NetEndHold clears _parked when the host resumes the body");
    remote.update(false, 0, false);
    assert!(!remote.parked);
}

#[test]
fn settled_unattended_untowed_hulls_sleep_but_coasting_or_towed_ones_do_not() {
    let slow = Velocity { linear: Vec3::new(0.5, 0., 0.5), angular: Vec3::splat(0.05) };
    let coasting = Velocity { linear: Vec3::Z * 3., angular: Vec3::ZERO };
    assert!(settled(true, false, &slow));
    assert!(!settled(true, false, &coasting));
    assert!(!settled(false, false, &slow), "a driven hull never sleeps");
    assert!(!settled(true, true, &slow), "a towed trailer is never held");
}
