use super::*;
use rules::*;

fn def(json: Value) -> Definition {
    let mut base = json!({"id":1,"name":"Fixture","size":{"x":1,"y":0.5,"z":2},"offset":0.75,
        "radius":0.5,"range":4,"upright":false,"mount":"Floor","fixture":"None","isStorage":false,
        "isWindTurbine":false,"procBox":true,"meshEuler":{"x":0,"y":0,"z":0}});
    for (k, v) in json.as_object().unwrap() {
        base[k] = v.clone();
    }
    serde_json::from_value(base).unwrap()
}
fn probe(point: Vec3, normal: Vec3) -> Probe {
    Probe {
        point,
        normal,
        clear: true,
        attach: true,
    }
}

#[test]
fn source_surface_gates_per_mount_family() {
    assert!(surface_ok(Mount::Floor, Vec3::new(0., 0.01, 1.)));
    assert!(!surface_ok(Mount::Floor, Vec3::new(0., 0.0099, 1.)));
    assert!(surface_ok(Mount::Wall, Vec3::new(1., 0.0999, 0.)));
    assert!(!surface_ok(Mount::Wall, Vec3::new(1., 0.1, 0.)));
    assert!(surface_ok(Mount::Ceiling, Vec3::new(0., -0.01, 1.)));
    assert!(!surface_ok(Mount::Ceiling, Vec3::Y));
    assert!(surface_ok(Mount::Sticky, Vec3::NEG_Y));
    assert_eq!(surface_reason(Mount::Floor), "Needs flat ground");
    assert_eq!(surface_reason(Mount::Wall), "Needs a wall");
    assert_eq!(surface_reason(Mount::Ceiling), "Needs flat ground");
}

#[test]
fn aim_reasons_follow_the_source_precedence_and_yaw_rules() {
    let floor = def(json!({}));
    let from = Vec3::new(0., 2., 0.);
    let dir = Vec3::NEG_Z;
    // Nothing within Range: parked along the ray, "Too far away", aim yaw.
    let aim = resolve(&floor, Mount::Floor, from, dir, 30., 90., None, 0.);
    assert!(!aim.valid);
    assert_eq!(aim.reason, Some(TOO_FAR));
    assert_eq!(aim.point, from + dir * 4.);
    assert_eq!((aim.normal, aim.yaw), (Vec3::Y, 30.));
    // A legal floor: valid, the aim yaw (camera + R offset) is used.
    let aim = resolve(&floor, Mount::Floor, from, dir, 120., 90., Some(probe(Vec3::NEG_Z * 3., Vec3::Y)), 0.);
    assert!(aim.valid && aim.reason.is_none());
    assert_eq!(aim.yaw, 120.);
    // The surface rule outranks an obstruction; an obstruction outranks attach.
    let mut p = probe(Vec3::ZERO, Vec3::Z);
    p.clear = false;
    p.attach = false;
    assert_eq!(resolve(&floor, Mount::Floor, from, dir, 0., 0., Some(p), 0.).reason, Some("Needs flat ground"));
    p.normal = Vec3::Y;
    assert_eq!(resolve(&floor, Mount::Floor, from, dir, 0., 0., Some(p), 0.).reason, Some(NO_ROOM));
    p.clear = true;
    assert_eq!(resolve(&floor, Mount::Floor, from, dir, 0., 0., Some(p), 0.).reason, Some(CANNOT_BUILD));
    // Wall: the wall's outward facing and nothing else (R never reaches it).
    let wall = def(json!({"mount":"Wall"}));
    let aim = resolve(&wall, Mount::Wall, from, dir, 77., 90., Some(probe(Vec3::ZERO, Vec3::X)), 0.);
    assert!(aim.valid);
    assert!((aim.yaw - 90.).abs() < 1e-4);
    let aim = resolve(&wall, Mount::Wall, from, dir, 77., 90., Some(probe(Vec3::ZERO, Vec3::Y)), 0.);
    assert_eq!(aim.reason, Some("Needs a wall"));
    // Sticky: only the manual spin.
    assert_eq!(resolve(&wall, Mount::Sticky, from, dir, 77., 180., Some(probe(Vec3::ZERO, Vec3::Y)), 0.).yaw, 180.);
    // The submersible band.
    let inlet = def(json!({"waterDepthMin":0.6,"waterDepthMax":5}));
    let at = |y: f32| resolve(&inlet, Mount::Floor, from, dir, 0., 0., Some(probe(Vec3::Y * y, Vec3::Y)), 10.);
    assert_eq!(at(9.5).reason, Some("Needs deeper water"));
    assert_eq!(at(4.).reason, Some("Water too deep"));
    assert!(at(7.).valid);
    // An ordinary def ignores water entirely (BarricadePlacer has no sea rule).
    assert!(resolve(&floor, Mount::Floor, from, dir, 0., 0., Some(probe(Vec3::Y * -50., Vec3::Y)), 10.).valid);
    // A window opening is never found natively: red, parked, yaw 0.
    let window = def(json!({"mount":"Window"}));
    let aim = resolve(&window, Mount::Window, from, dir, 50., 0., Some(probe(Vec3::ZERO, Vec3::Y)), 0.);
    assert_eq!((aim.valid, aim.reason, aim.yaw), (false, Some("Needs a window opening"), 0.));
}

#[test]
fn ghost_and_accepted_seat_share_one_transform() {
    let bounds = geometry::Bounds {
        min: Vec3::new(-0.4, -0.3, -1.2),
        max: Vec3::new(0.5, 0.2, 0.9),
    };
    let point = Vec3::new(3., 1.5, -7.);
    for (json, normal) in [
        (json!({}), Vec3::Y),
        (json!({"upright":true}), Vec3::Y),
        (json!({"mount":"Wall"}), Vec3::new(0.6, 0., -0.8)),
        (json!({"mount":"Ceiling","offset":0.3}), Vec3::NEG_Y),
    ] {
        let d = def(json);
        let mount = Mount::of(&d);
        let yaw = if mount == Mount::Wall { yaw_facing(normal) } else { 37. };
        let aim = Aim {
            valid: true,
            reason: None,
            point,
            normal,
            yaw,
        };
        let ghost = ghost_transform(&d, bounds, mount, &aim);
        let placed = geometry::seat(
            &d,
            bounds,
            Pose {
                point,
                yaw,
                mount_up: false,
            },
        );
        assert!(ghost.translation.distance(placed.translation) < 1e-5, "{:?}", d.mount);
        assert!(ghost.rotation.angle_between(placed.rotation) < 1e-5, "{:?}", d.mount);
    }
}
