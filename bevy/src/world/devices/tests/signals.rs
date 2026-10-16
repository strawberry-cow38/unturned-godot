//! Checkpoint 3: traffic signals and the car lift.
use super::*;
use traffic::{CYCLE_SEC, Head, Phase, lens_index, phase_at, split_heads};

#[test]
fn a_powered_head_cycles_green_amber_red_and_is_never_dark() {
    let mut seen = vec![];
    for i in 0..(CYCLE_SEC * 10.) as i32 {
        let p = phase_at(f64::from(i) * 0.1, 0.);
        assert!(lens_index(p).is_some(), "a powered head is never dark");
        if seen.last() != Some(&p) {
            seen.push(p);
        }
    }
    assert_eq!(seen, [Phase::Green, Phase::Amber, Phase::Red]);
    assert_eq!(phase_at(24.9, 0.), Phase::Green);
    assert_eq!(phase_at(25., 0.), Phase::Amber);
    assert_eq!(phase_at(28.9, 0.), Phase::Amber, "AmberSec 4: real signals hold amber 3-5 s");
    assert_eq!(phase_at(30.9, 0.), Phase::Red, "AllRedSec 2");
    assert_eq!(phase_at(29., 0.), Phase::Red);
    assert_eq!(phase_at(31., 0.), Phase::Green);
    assert_eq!(phase_at(0., 25.5), Phase::Amber, "the offset shifts the cycle");
    // FlashRed lights the RED lens, never green (the bug the single map exists for).
    assert_eq!(lens_index(Phase::FlashRed), Some(0));
    assert_eq!(lens_index(Phase::FlashAmber), Some(1));
    assert_eq!(lens_index(Phase::Off), None);
}

#[test]
fn each_head_has_its_own_deterministic_offset() {
    let at = Vec3::new(235.2, 35.9, 105.4);
    let (a, b) = (traffic::offset(at, 0), traffic::offset(at, 1));
    assert!((0. ..CYCLE_SEC).contains(&a) && (0. ..CYCLE_SEC).contains(&b));
    assert_ne!(a, b, "the two heads of one mast drift apart");
    assert_eq!(traffic::offset(at, 0), a);
    assert_eq!(traffic::offset(Vec3::new(235.2, 0., 105.4), 0), a, "Y is not hashed");
}

#[test]
fn the_mast_splits_into_two_heads_of_three_lenses_along_its_y() {
    let data = obj("Traffic_Light_0");
    let (heads, body) = split_heads(&data).unwrap();
    assert_eq!(heads.len(), 2);
    let lensed: usize = heads.iter().flatten().flatten().map(|d| d.positions.len() / 3).sum();
    assert_eq!(body.positions.len() / 3 + lensed, data.positions.len() / 3);
    let span = |h: &[Option<crate::content::ObjData>; 3]| {
        h.iter().map(|l| l.as_ref().expect("red, amber and green on every head")).fold(
            (f32::INFINITY, f32::NEG_INFINITY),
            |(lo, hi), l| (lo.min(l.min.y), hi.max(l.max.y)),
        )
    };
    let (low, high) = (span(&heads[0]), span(&heads[1]));
    assert!(low.1 + 0.5 < high.0, "ordered low to high along mesh Y with a gap: {low:?} {high:?}");
}

/// The shipped `traffic_side_roads.txt` is deliberately empty (its rows are
/// commented out as "not active"), so every PEI signal flashes amber. The
/// commented rows still name real masts in the file's key convention.
#[test]
fn peis_signals_are_all_main_road_and_the_side_road_key_matches_the_file() {
    let root = content();
    let text = std::fs::read_to_string(root.join("objects/traffic_side_roads.txt")).unwrap();
    let names = crate::content::load_guid_map(&root).unwrap();
    let placements = crate::world::parse_placements(&std::fs::read_to_string(root.join("objects/placements.txt")).unwrap(), &names).unwrap();
    let signals: Vec<_> = placements.iter().filter(|p| p.name == "Traffic_Light_0").collect();
    assert_eq!(signals.len(), 21);
    let active = traffic::load_side_roads(&text);
    assert!(active.is_empty(), "no active side-road rows ship");
    let commented: Vec<&str> = text.lines().map(|l| l.trim_start_matches('#')).collect();
    let keys = traffic::load_side_roads(&commented.join("\n"));
    assert_eq!(keys.len(), 9);
    let matched = signals.iter().filter(|p| keys.contains(&traffic::side_road_key(p.transform.translation))).count();
    assert_eq!(matched, 9, "keyed in placements.txt coordinates (Z un-negated)");
}

#[test]
fn losing_the_mains_breathes_the_flash_then_the_battery_dies_with_a_stutter() {
    let day = 120.;
    let mut head = Head::for_test(0., false);
    head.tick(10., 0.02);
    assert_eq!(head.phase, Phase::Green);
    head.set_powered(false, 10., day);
    head.tick(10.5, 0.02);
    assert_eq!(head.phase, Phase::FlashAmber, "main road: amber");
    let mut levels = vec![];
    for i in 0..100 {
        head.tick(10.5 + f64::from(i) * 0.02, 0.02);
        levels.push(head.level);
    }
    assert!(levels.iter().any(|l| *l > 0.9) && levels.iter().any(|l| *l < 0.1), "a slow breath, not a hard blink");
    // Two in-game days on the battery, then a 2.5 s stutter into darkness.
    let dead = 10. + 2. * day;
    head.tick(dead - 1., 0.02);
    assert_eq!(head.phase, Phase::FlashAmber);
    head.tick(dead + 1., 0.02);
    assert_eq!(head.phase, Phase::FlashAmber, "still stuttering");
    head.tick(dead + 2.6, 0.02);
    assert_eq!(head.phase, Phase::Off);
    // Power back: the cycle resumes and the battery recharges.
    head.set_powered(true, dead + 3., day);
    head.tick(dead + 3., 0.02);
    assert!(lens_index(head.phase).is_some() && !matches!(head.phase, Phase::FlashAmber | Phase::FlashRed));
    let mut side = Head::for_test(0., true);
    side.set_powered(false, 0., day);
    side.tick(1., 0.02);
    assert_eq!(side.phase, Phase::FlashRed, "a side road flashes red");
}

#[test]
fn the_ramp_box_joins_the_authority_geometry_at_the_lifts_pose() {
    let ramp = obj(carlift::RAMP);
    let lift = crate::world::placement_transform(Vec3::new(10., 2., -5.), Vec3::new(270., 30., 0.), Vec3::ONE);
    let body = crate::world::car_lift_ramp(lift, &ramp);
    // The box's corners are the mesh AABB's, through the lift's transform.
    let corners: Vec<Vec3> = (0..8)
        .map(|i| {
            lift.transform_point(Vec3::new(
                if i & 1 == 0 { ramp.min.x } else { ramp.max.x },
                if i & 2 == 0 { ramp.min.y } else { ramp.max.y },
                if i & 4 == 0 { ramp.min.z } else { ramp.max.z },
            ))
        })
        .collect();
    let min = corners.iter().fold(Vec3::splat(f32::INFINITY), |a, b| a.min(*b));
    let max = corners.iter().fold(Vec3::splat(f32::NEG_INFINITY), |a, b| a.max(*b));
    assert!(body.min.distance(min) < 1e-4 && body.max.distance(max) < 1e-4, "{:?}..{:?} vs {min}..{max}", body.min, body.max);
    assert_eq!(body.geometry.positions.len(), 36, "a box, as BoxShape3D");
}

/// A retail mast and a car lift side by side through the production systems.
fn signal_app() -> (App, Entity, Entity) {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        bevy_rapier3d::prelude::RapierPhysicsPlugin::<bevy_rapier3d::prelude::NoUserData>::default()
            .in_fixed_schedule(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(Duration::from_millis(20)))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .insert_resource(GameSnapshot(json!({"player":{"alive":true},
        "deployables":[{"netId":1,"defId":9200,"toggledOn":true}],
        "worldClock":{"day":0,"timeOfDay":0.0,"dayLengthSeconds":1800.0}})))
    .insert_resource(catalog())
    .insert_resource(WorldConfig::default())
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<InteractionFocus>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_resource::<DeviceState>()
    .init_resource::<Mains>()
    .init_resource::<traffic::SideRoads>()
    .configure_sets(Update, crate::entities::ApplyInteractions.after(CollectInteractions))
    .add_systems(
        Update,
        (
            mains,
            (traffic::attach, carlift::attach),
            traffic::tick,
            |mut f: ResMut<InteractionFocus>| *f = default(),
        )
            .chain()
            .before(CollectInteractions),
    )
    .add_systems(Update, collect_focus.in_set(CollectInteractions));
    let world = app.world_mut();
    let material = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial::default());
    let mut place = |name: &str, index: usize, at: Vec3| {
        let data = obj(name);
        let e = Vec3::new(270., 180., 0.);
        let mesh = world.resource_mut::<Assets<Mesh>>().add(data.mesh());
        world
            .spawn((
                Mesh3d(mesh),
                MeshMaterial3d(material.clone()),
                crate::world::placement_transform(at, e, Vec3::ONE),
                WorldObject { guid: String::new(), asset_name: name.into(), source_index: index },
                PlacementEuler(e),
                bevy_rapier3d::prelude::RigidBody::Fixed,
            ))
            .id()
    };
    let mast = place("Traffic_Light_0", 3125, Vec3::new(40., 0., 0.));
    let lift = place(carlift::PROP, 900, Vec3::ZERO);
    // Looking down at the platform from a standing height in front of the lift.
    world.spawn((PlayerCamera, Transform::from_xyz(0., 1.7, 1.2).looking_at(Vec3::new(0., 0.2, 0.), Vec3::Y)));
    (app, mast, lift)
}

#[test]
fn a_mast_runs_two_heads_off_the_world_clock_and_the_lift_focuses_without_a_rim() {
    let (mut app, mast, _) = signal_app();
    step(&mut app, 4);
    let mut heads = app.world_mut().query::<(&Head, &ChildOf)>();
    let found: Vec<_> = heads.iter(app.world()).filter(|(_, p)| p.parent() == mast).map(|(h, _)| (h.offset, h.phase)).collect();
    assert_eq!(found.len(), 2, "one TrafficLight per head");
    let at = crate::world::placement_transform(Vec3::new(40., 0., 0.), Vec3::new(270., 180., 0.), Vec3::ONE).translation;
    for (h, (offset, phase)) in found.iter().enumerate() {
        assert_eq!(*offset, traffic::offset(at, h));
        assert_eq!(*phase, phase_at(0., *offset), "phase = the accepted clock (day + time) x day length + offset");
    }
    // The clock moves 12.4 s later: the phase follows the replicated value, not frame time.
    app.world_mut().resource_mut::<GameSnapshot>().0["worldClock"]["timeOfDay"] = json!(12.4 / 1800.);
    step(&mut app, 1);
    for (h, _) in heads.iter(app.world()).filter(|(_, p)| p.parent() == mast) {
        assert_eq!(h.phase, phase_at(12.4_f32 as f64, h.offset));
    }
    // The ray lands on the platform: the car lift's silent focus, and no rim.
    let focus = app.world().resource::<InteractionFocus>();
    assert!(focus.selected_args("carLift", &json!({"sourceIndex":900})));
    assert_eq!(focus.outlined(), None, "_focusCarLift claims no outline");
}
