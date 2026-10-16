use super::*;
use super::super::tests::{body, physics_app};

fn jeep() -> &'static data::VehicleSpec {
    &data::named("jeep").unwrap().spec
}

#[test]
fn source_tow_points_sit_on_the_box_faces_at_low_bumper_height() {
    let s = &data::named("semi").unwrap().spec;
    // BoxCenter (0,.825,-.54), BoxSize (3.18,1.35,4.08) in Vehicle.Build terms.
    assert!(tow_local(s, false).distance(Vec3::new(0., 0.42, -2.73)) < 1e-5);
    assert!(tow_local(s, true).distance(Vec3::new(0., 0.42, 1.65)) < 1e-5);
}

#[test]
fn tension_only_pulls_past_rest_with_source_spring_damper_and_clamp() {
    let tower = End { pose: Transform::IDENTITY, velocity: Vec3::ZERO, spec: jeep() };
    let gap = |z: f32| tow_local(jeep(), false).z + z - tow_local(jeep(), true).z;
    let at = |z: f32| End { pose: Transform::from_xyz(0., 0., z), ..tower };
    // Towed car 3 m of rope behind: slack at rest 3.5, taut past it.
    let z = 3. + tow_local(jeep(), true).z - tow_local(jeep(), false).z;
    assert!((gap(z) - 3.).abs() < 1e-4);
    assert!(tension(&tower, &at(z), 3.5).is_none(), "a rope never pushes");
    let (f, a, b) = tension(&tower, &at(z), 2.5).unwrap();
    assert!((f.length() - TOW_STIFFNESS * 0.5).abs() < 1e-2, "{f}");
    assert!(f.z > 0., "the tower is pulled back toward the towed car: {f}");
    assert!(a.distance(tower.point(true)) < 1e-5 && b.distance(at(z).point(false)) < 1e-5);
    let separating = End { velocity: Vec3::Z * 2., ..at(z) };
    let damped = tension(&tower, &separating, 2.5).unwrap().0.length();
    assert!((damped - (TOW_STIFFNESS * 0.5 + TOW_DAMPING * 2.)).abs() < 1e-1);
    let closing = End { velocity: Vec3::NEG_Z * 10., ..at(z) };
    assert!(tension(&tower, &closing, 2.5).is_some_and(|t| t.0.length() == 0.), "damping cannot push");
    let yanked = tension(&tower, &at(z + 5.), 2.5).unwrap().0.length();
    assert_eq!(yanked, TOW_MAX_FORCE);
}

#[test]
fn rope_sags_by_half_its_slack_plus_baseline_and_ends_at_the_tow_points() {
    let (a, b) = (Vec3::new(0., 1., 0.), Vec3::new(0., 1., 2.));
    let slack = sag_points(a, b, 3.);
    assert_eq!((slack[0], slack[ROPE_SEGMENTS]), (a, b));
    assert!((slack[ROPE_SEGMENTS / 2].y - (1. - 0.56)).abs() < 1e-5, "(3-2)*.5+.06 at the middle");
    let taut = sag_points(a, b, 1.5);
    assert!((taut[ROPE_SEGMENTS / 2].y - (1. - 0.06)).abs() < 1e-5, "taut keeps the 6 cm baseline");
}

fn rig(roped: bool) -> (App, Entity, Entity) {
    let mut app = physics_app(None);
    app.world_mut().spawn((Collider::cuboid(500., 0.5, 500.), Transform::from_xyz(0., -0.5, 0.)));
    let tower = body(&mut app, "jeep", Vec3::new(0., 1.8, 0.));
    // Towed jeep parked behind (+Z) with 3 m between the rear and front nodes.
    let behind = tow_local(jeep(), true).z + 3. - tow_local(jeep(), false).z;
    let towed = body(&mut app, "jeep", Vec3::new(0., 1.8, behind));
    {
        let mut b = app.world_mut().get_mut::<VehicleBody>(towed).unwrap();
        b.net_id = 2;
        b.driver_player_id = 0;
        b.controls = default();
        if roped {
            b.rope = Tow { towed: 0, rest: 0., by: 1 };
        }
    }
    if roped {
        app.world_mut().get_mut::<VehicleBody>(tower).unwrap().rope = Tow { towed: 2, rest: 3., by: 0 };
    }
    for _ in 0..100 {
        app.update();
    }
    (app, tower, towed)
}
fn drive(app: &mut App, tower: Entity, towed: Entity) -> (f32, f32) {
    let start = app.world().get::<Transform>(towed).unwrap().translation.z;
    app.world_mut().get_mut::<VehicleBody>(tower).unwrap().controls.throttle = 1.;
    let mut max_gap = 0f32;
    for _ in 0..250 {
        app.update();
        let a = app.world().get::<Transform>(tower).unwrap().transform_point(tow_local(jeep(), true));
        let b = app.world().get::<Transform>(towed).unwrap().transform_point(tow_local(jeep(), false));
        max_gap = max_gap.max(a.distance(b));
    }
    (start - app.world().get::<Transform>(towed).unwrap().translation.z, max_gap)
}

#[test]
fn accepted_rope_drags_a_free_rolling_parked_car_behind_a_driving_tower() {
    let (mut app, tower, towed) = rig(true);
    let slip = app.world().get::<RayCastVehicleController>(towed).unwrap().wheels[0].tuning.friction_slip;
    assert_eq!(slip, TOWED_FREE_ROLL_SLIP, "SetTowedFreeRoll");
    let (pulled, gap) = drive(&mut app, tower, towed);
    assert!(pulled > 8., "the rope drags the towed car forward: {pulled} m");
    assert!(gap < 7.5, "a steady tow stays short of the source break length: {gap} m");
    let (mut control, tower, towed) = rig(false);
    let (left, _) = drive(&mut control, tower, towed);
    assert!(left.abs() < 0.5, "without the accepted rope the parked car stays braked: {left} m");
}

#[test]
fn source_apply_force_arm_matches_the_recorded_godot_jolt_lever_and_doubles_the_com_offset() {
    // Godot ApplyForce(F, o): torque = (o - R·com) × F, recorded on 4.6 Jolt.
    let rows: Vec<serde_json::Value> = serde_json::from_str(include_str!(
        "../../../data/apply-force-arm-reference.json"
    ))
    .unwrap();
    let v = |r: &serde_json::Value, k: &str| {
        Vec3::new(r[k][0].as_f64().unwrap() as f32, r[k][1].as_f64().unwrap() as f32, r[k][2].as_f64().unwrap() as f32)
    };
    assert_eq!(rows.len(), 4);
    for r in &rows {
        let yaw = Quat::from_rotation_y((r["yaw"].as_f64().unwrap() as f32).to_radians());
        let torque = (v(r, "offset") - yaw * v(r, "com")).cross(v(r, "force"));
        let observed = v(r, "angularVelocity") * r["inertia"].as_f64().unwrap() as f32 / r["dt"].as_f64().unwrap() as f32;
        assert!(observed.distance(torque) < 0.05, "{r}: {observed} vs {torque}");
    }
    // UpdateTow passes o = point - ToGlobal(CoM); `apply` must give that torque.
    let spec = jeep();
    let pose = Transform::from_xyz(3., 1., -2.).with_rotation(Quat::from_euler(EulerRot::YXZ, 0.7, 0.1, -0.05));
    let end = End { pose, velocity: Vec3::ZERO, spec };
    let (point, f) = (pose.transform_point(tow_local(spec, true)), Vec3::new(400., 20., -900.));
    let com = pose.rotation * spec.center_of_mass().0;
    let source = ((point - pose.transform_point(spec.center_of_mass().0)) - com).cross(f);
    let mut force = ExternalForce::default();
    apply(&mut force, &end, f, point);
    assert_eq!(force.force, f);
    assert!(force.torque.distance(source) < 1e-2, "{} vs {source}", force.torque);
    assert!(com.length() > 0.1 && force.torque.distance((point - end.pose.transform_point(spec.center_of_mass().0)).cross(f)) > 1.,
        "the port must not silently use the true CoM arm");
}

#[test]
fn towed_free_roll_saves_and_restores_a_tracked_hull_slip_it_never_rewrites() {
    let tank = &data::named("tank").unwrap().spec;
    assert!(tank.tracked);
    let mut wheels = tank.controller();
    let mut saved = SavedSlip::default();
    saved.free_roll(true, &mut wheels);
    saved.free_roll(true, &mut wheels);
    assert!(wheels.wheels.iter().all(|w| w.tuning.friction_slip == TOWED_FREE_ROLL_SLIP));
    saved.free_roll(false, &mut wheels);
    assert!(wheels.wheels.iter().all(|w| w.tuning.friction_slip == 1.), "SetTowedFreeRoll(false) restores the tracks");
    assert_eq!(saved, SavedSlip::default());
}

#[test]
fn rope_tower_engine_is_nerfed_to_nine_tenths_and_a_taut_rope_wakes_the_parked_towed_car() {
    let (mut roped, tower, towed) = rig(true);
    let (mut control, c_tower, c_towed) = rig(false);
    for (app, tower) in [(&mut roped, tower), (&mut control, c_tower)] {
        app.world_mut().get_mut::<VehicleBody>(tower).unwrap().controls.throttle = 1.;
        app.update();
    }
    let engine = |app: &App, e: Entity| {
        app.world().get::<RayCastVehicleController>(e).unwrap().wheels.iter().map(|w| w.engine_force).collect::<Vec<_>>()
    };
    let (nerfed, full) = (engine(&roped, tower), engine(&control, c_tower));
    assert!(full.iter().any(|f| f.abs() > 1.), "{full:?}");
    for (n, f) in nerfed.iter().zip(&full) {
        assert!((n - f * 0.9).abs() < 1e-3, "Towing: eng *= 0.9: {nerfed:?} vs {full:?}");
    }
    assert!(roped.world().get::<VehicleBody>(towed).unwrap().park.parked, "slack: still parked");
    drive(&mut roped, tower, towed);
    drive(&mut control, c_tower, c_towed);
    assert!(!roped.world().get::<VehicleBody>(towed).unwrap().park.parked, "UpdateTow Wake()s the towed car");
    assert!(control.world().get::<VehicleBody>(c_towed).unwrap().park.parked);
}

fn taut(tower_key: &str, towed_key: &str, roped: bool) -> (App, Entity, Entity) {
    let mut app = physics_app(None);
    let (a, b) = (&data::named(tower_key).unwrap().spec, &data::named(towed_key).unwrap().spec);
    let tower = body(&mut app, tower_key, Vec3::new(0., 3., 0.));
    let towed = body(&mut app, towed_key, Vec3::new(0., 3., tow_local(a, true).z + 4. - tow_local(b, false).z));
    {
        let mut t = app.world_mut().get_mut::<VehicleBody>(towed).unwrap();
        t.net_id = 2;
        t.driver_player_id = 0;
        t.controls = default();
        t.rope = Tow { towed: 0, rest: 0., by: if roped { 1 } else { 0 } };
    }
    {
        let mut t = app.world_mut().get_mut::<VehicleBody>(tower).unwrap();
        t.driver_player_id = 0;
        t.controls = default();
        t.rope = Tow { towed: if roped { 2 } else { 0 }, rest: 2., by: 0 };
    }
    app.update();
    (app, tower, towed)
}

#[test]
fn an_aircraft_tower_never_pulls_but_a_car_pulls_a_towed_aircraft() {
    let force = |app: &App, e: Entity| *app.world().get::<ExternalForce>(e).unwrap();
    let (heli_tower, heli, car) = taut("huey", "jeep", true);
    let (control, c_heli, c_car) = taut("huey", "jeep", false);
    assert_eq!(force(&heli_tower, car), force(&control, c_car), "StepHeli returns before UpdateTow");
    assert_eq!(force(&heli_tower, heli), force(&control, c_heli), "...so the aircraft tower is not pulled either");
    let (car_tower, _, heli) = taut("jeep", "huey", true);
    let (control, _, c_heli) = taut("jeep", "huey", false);
    let pull = force(&car_tower, heli).force - force(&control, c_heli).force;
    assert!(pull.z < -1000., "the car's UpdateTow drags the towed aircraft forward: {pull}");
    let slip = car_tower.world().get::<RayCastVehicleController>(heli).unwrap().wheels.iter().all(|w| w.tuning.friction_slip == TOWED_FREE_ROLL_SLIP);
    assert!(slip, "SetTowedFreeRoll walks every wheel node, aircraft included");
}

#[test]
fn an_accepted_rope_is_a_mutual_collision_exception_and_an_unroped_pair_is_not() {
    for roped in [true, false] {
        let (mut app, tower, towed) = taut("jeep", "jeep", roped);
        app.init_resource::<super::super::coupling::physics::Exceptions>();
        super::super::coupling::physics::approach(app.world_mut());
        let exceptions = app.world().resource::<super::super::coupling::physics::Exceptions>();
        assert_eq!(exceptions.contains(towed, tower), roped);
    }
}

#[test]
fn tow_length_readout_rounds_midpoints_away_from_zero_like_dotnet() {
    assert_eq!(tool::dotnet_one_decimal(2.25), "2.3");
    assert_eq!(tool::dotnet_one_decimal(0.75), "0.8");
    assert_eq!(tool::dotnet_one_decimal(3.), "3.0");
}
