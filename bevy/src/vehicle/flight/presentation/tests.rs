use super::*;
use crate::vehicle::data;

#[test]
fn source_rates_damage_and_ignition_gate_are_shared_without_advancing_flight() {
    for key in ["huey", "otter"] {
        let spec = &data::named(key).unwrap().spec;
        let mut visual = Flight::new(spec, 10.);
        visual.main_health = 0.25;
        visual.tail_health = 0.75;
        visual.collective = 0.42;
        visual.lift_acceleration = Vec3::new(1., 2., 3.);
        let mut physical = visual.clone();
        let mut wheels = spec.controller();
        let mut accumulated_spool = 0_f64;
        for tick in 1..=50 {
            visual.advance_presentation(spec, true, false, 0.02);
            physical.step(
                spec,
                &Transform::IDENTITY,
                &Velocity::default(),
                Controls {
                    engine: true,
                    ..default()
                },
                AirContact::default(),
                &mut wheels,
                false,
                false,
                0.02,
            );
            let expected_spool = f64::from(tick) * 0.02 / if spec.heli { 7.4 } else { 3.2 };
            accumulated_spool += expected_spool;
            assert!((f64::from(visual.spool) - expected_spool).abs() < 0.00001);
            for (actual, rate) in [
                (visual.main_phase, 46. * (0.30 + 0.70 * 0.25)),
                (visual.tail_phase, 120. * (0.30 + 0.70 * 0.75)),
                (visual.prop_phase, 150.),
            ] {
                let expected = (accumulated_spool * 0.02 * rate) % std::f64::consts::TAU;
                assert!(
                    (f64::from(actual) - expected).abs() < 0.0001,
                    "{key} tick{tick}"
                );
            }
            assert_eq!(visual.spool, physical.spool);
            assert_eq!(visual.main_phase, physical.main_phase);
            assert_eq!(visual.tail_phase, physical.tail_phase);
            assert_eq!(visual.prop_phase, physical.prop_phase);
        }
        assert_eq!(
            visual.collective, 0.42,
            "presentation must not run throttle/flight"
        );
        assert_eq!(visual.lift_acceleration, Vec3::new(1., 2., 3.));
        assert_eq!(visual.grace, Flight::new(spec, 10.).grace);
        let phases = (visual.main_phase, visual.tail_phase, visual.prop_phase);
        visual.advance_presentation(spec, true, true, 0.02);
        assert_eq!(
            (visual.main_phase, visual.tail_phase, visual.prop_phase),
            phases,
            "source wreck stops blades immediately, even while RPM decays"
        );
    }
}

#[test]
fn aircraft_ignition_rearms_only_after_full_stop_and_warm_npc_never_cranks() {
    let spec = &data::named("huey").unwrap().spec;
    let mut flight = Flight::new(spec, 10.);
    for _ in 0..250 {
        flight.advance_presentation(spec, true, false, 0.02);
    }
    assert_eq!(flight.ignition_starts, 1);
    for _ in 0..20 {
        flight.advance_presentation(spec, false, false, 0.02);
    }
    flight.advance_presentation(spec, true, false, 0.02);
    assert_eq!(
        flight.ignition_starts, 1,
        "mid-spool switch flip is not a cold start"
    );
    for _ in 0..300 {
        flight.advance_presentation(spec, false, false, 0.02);
    }
    assert_eq!(flight.spool, 0.);
    flight.advance_presentation(spec, true, false, 0.02);
    assert_eq!(flight.ignition_starts, 2);
    assert_eq!(flight.ignition_remaining, 7.4);
    let mut warm = Flight::new(spec, 10.);
    warm.spawn_rotor_running();
    warm.advance_presentation(spec, true, false, 0.02);
    assert_eq!(warm.ignition_starts, 0);
    assert_eq!(warm.spool, 1.);
    assert_eq!(warm.ignition_remaining, 0.);
    assert!((warm.main_phase - 0.92).abs() < 0.00001);
}
