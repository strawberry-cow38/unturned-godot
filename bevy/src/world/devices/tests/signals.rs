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
    .init_resource::<traffic::Palette>()
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

/// The production device schedule on a `--dedicated` host: the source builds
/// none of the presentation devices there (`mode != WorldMode.Dedicated`), but
/// the car lift's platform is a collider "in EVERY MODE". The gated systems'
/// resources (focus, keys, snapshot, UI) are deliberately absent: a system
/// that ran anyway would fail its parameter validation.
#[test]
fn a_dedicated_host_builds_only_the_car_lift_platform() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .insert_resource(WorldConfig::default())
        .init_resource::<crate::content::ContentCache>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(crate::LaunchOptions(crate::cli::Options {
            network: crate::network::Target::Dedicated { port: 47872, name: None, max_players: None },
            play: true,
            ..default()
        }));
    super::super::systems(&mut app);
    let world = app.world_mut();
    let material = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial::default());
    for (name, index, x) in [("Traffic_Light_0", 1, 40.), (carlift::PROP, 2, 0.), ("Light_0", 3, -8.), ("Vendor_0", 4, 8.)] {
        let mesh = world.resource_mut::<Assets<Mesh>>().add(obj(name).mesh());
        let e = Vec3::new(270., 180., 0.);
        world.spawn((
            Mesh3d(mesh),
            MeshMaterial3d(material.clone()),
            crate::world::placement_transform(Vec3::new(x, 0., 0.), e, Vec3::ONE),
            WorldObject { guid: String::new(), asset_name: name.into(), source_index: index },
            PlacementEuler(e),
        ));
    }
    step(&mut app, 4);
    let world = app.world_mut();
    assert_eq!(world.query::<&carlift::Ramp>().iter(world).count(), 1, "the platform exists on the server");
    assert_eq!(
        world.query_filtered::<(), (With<bevy_rapier3d::prelude::Collider>, With<ChildOf>)>().iter(world).count(),
        1,
        "and is a collider"
    );
    assert_eq!(world.query::<&Head>().iter(world).count(), 0, "no traffic heads");
    assert_eq!(world.query::<&lamps::LampFixture>().iter(world).count(), 0, "no lamp lights");
    assert_eq!(world.query::<&vending::Vendor>().iter(world).count(), 0, "no vending machine");
    assert!(world.query::<&PointLight>().iter(world).next().is_none(), "no device light at all");
}

/// A joined client's snapshot has no `day` (`RemoteSession.ClockSnapshot`):
/// the time of day wraps every day, and the battery deadline two days out
/// must still be reached, so the fallback counts the wraps itself.
#[test]
fn a_dayless_clock_counts_its_own_wraps_so_the_battery_still_dies() {
    let length = 120.;
    let mut clock = traffic::Clock::default();
    let snap = |time: f64| json!({"worldClock":{"day":null,"timeOfDay":time,"dayLengthSeconds":length}});
    let (start, _) = clock.seconds(&snap(0.9), 0.);
    let mut head = Head::for_test(0., false);
    head.set_powered(false, start, length);
    let mut last = start;
    let mut dark_at = None;
    // Three and a half days in 0.01-day steps, wrapping at midnight.
    for step in 1..=350 {
        let time = (0.9 + f64::from(step) * 0.01).rem_euclid(1.);
        let (now, _) = clock.seconds(&snap(time), 0.);
        assert!(now > last, "monotonic across the wrap at step {step}: {now} after {last}");
        last = now;
        head.tick(now, 0.02);
        if head.phase == Phase::Off && dark_at.is_none() {
            dark_at = Some(now - start);
        }
    }
    let dark = dark_at.expect("the battery dies on a day-less clock");
    assert!((2. * length..2. * length + 4.).contains(&dark), "two days plus the stutter: {dark}");
    // A replicated day takes over from the local count.
    let replicated = json!({"worldClock":{"day":7,"timeOfDay":0.25,"dayLengthSeconds":length}});
    assert_eq!(clock.seconds(&replicated, 0.).0, 7.25 * length);
}

/// Row 15A: a joined client's snapshot now carries the host's replicated
/// `worldClock.day` (System 21 base day, `RemoteEnvironment.DayAt`). A remote that
/// joined on day 3 has never seen a wrap, so without the day its fallback would
/// count from 0 and every head would run whole days behind the host. With the
/// remote-shaped snapshot the clock, and so every head's phase, equals the host's.
#[test]
fn a_joined_clients_replicated_day_puts_every_head_in_the_hosts_phase() {
    let length = 1440.;
    let host = json!({"worldClock":{"day":3,"timeOfDay":0.41816285,"dayLengthSeconds":length}});
    // The bridge's remote row: the same accepted day, time derived from the applied tick.
    let remote = json!({"worldClock":{"day":3,"timeOfDay":0.41816285,"dayLengthSeconds":length},
        "network":{"mode":"client","ownsWorld":false}});
    let (host_t, _) = traffic::Clock::default().seconds(&host, 0.);
    let (remote_t, _) = traffic::Clock::default().seconds(&remote, 0.);
    assert_eq!(remote_t, host_t);
    let dayless = json!({"worldClock":{"day":null,"timeOfDay":0.41816285,"dayLengthSeconds":length}});
    let (fallback_t, _) = traffic::Clock::default().seconds(&dayless, 0.);
    assert_eq!(host_t - fallback_t, 3. * length, "without the day a fresh joiner is whole days behind");
    let mut differs = false;
    for (n, at) in [Vec3::ZERO, Vec3::new(12.5, 0., -40.), Vec3::new(-300., 0., 87.)].into_iter().enumerate() {
        for head in 0..2 {
            let offset = traffic::offset(at, head);
            assert_eq!(phase_at(remote_t, offset), phase_at(host_t, offset), "head {head} of mast {n}");
            differs |= phase_at(fallback_t, offset) != phase_at(host_t, offset);
        }
    }
    assert!(differs, "the fixture is sensitive to the day: some head disagrees without it");
}

fn set_clock(app: &mut App, seconds: f64) {
    app.world_mut().resource_mut::<GameSnapshot>().0["worldClock"]["timeOfDay"] = json!(seconds / 1800.);
}
fn set_mains(app: &mut App, on: bool) {
    app.world_mut().resource_mut::<GameSnapshot>().0["deployables"][0]["toggledOn"] = json!(on);
}
fn mast_heads(app: &mut App, mast: Entity) -> Vec<(usize, f64)> {
    let mut q = app.world_mut().query::<(&Head, &ChildOf)>();
    let mut v: Vec<_> = q
        .iter(app.world())
        .filter(|(_, p)| p.parent() == mast)
        .map(|(h, _)| (h.index, h.battery_dead_at()))
        .collect();
    v.sort_by_key(|h| h.0);
    v
}

/// `build_world` respawns every `WorldObject` on a re-stream; the source's
/// nodes never stream, so a head on its battery keeps its deadline.
#[test]
fn a_restreamed_mast_keeps_its_battery_deadline() {
    let (mut app, mast, _) = signal_app();
    step(&mut app, 4);
    set_mains(&mut app, false);
    step(&mut app, 15);
    let before = mast_heads(&mut app, mast);
    assert_eq!(before.len(), 2);
    assert!(before.iter().all(|h| h.1 == 2. * 1800.), "stamped two days out at clock 0: {before:?}");
    // An hour later the region re-streams: the old mast goes, a new one comes.
    set_clock(&mut app, 3600.);
    step(&mut app, 2);
    let (mesh, material, xf, euler) = {
        let w = app.world();
        (
            w.get::<Mesh3d>(mast).unwrap().clone(),
            w.get::<MeshMaterial3d<StandardMaterial>>(mast).unwrap().clone(),
            *w.get::<Transform>(mast).unwrap(),
            *w.get::<PlacementEuler>(mast).unwrap(),
        )
    };
    app.world_mut().entity_mut(mast).despawn();
    let again = app
        .world_mut()
        .spawn((
            mesh,
            material,
            xf,
            WorldObject { guid: String::new(), asset_name: "Traffic_Light_0".into(), source_index: 3125 },
            euler,
        ))
        .id();
    step(&mut app, 4);
    assert_eq!(mast_heads(&mut app, again), before, "not re-stamped an hour later");
    // Mains back: the deadline is forgotten.
    set_mains(&mut app, true);
    step(&mut app, 15);
    assert!(mast_heads(&mut app, again).iter().all(|h| h.1 == f64::INFINITY));
    assert!(app.world().resource::<DeviceState>().signal_battery.is_empty());
}

#[derive(Resource, Default)]
struct Modified(usize);
fn count_modified(mut events: MessageReader<AssetEvent<StandardMaterial>>, mut n: ResMut<Modified>) {
    n.0 += events.read().filter(|e| matches!(e, AssetEvent::Modified { .. })).count();
}

/// The breathing flash swaps shared palette handles; it never rewrites a
/// material asset per frame.
#[test]
fn a_breathing_flash_never_mutates_a_material() {
    let (mut app, mast, _) = signal_app();
    app.init_resource::<Modified>().add_systems(Last, count_modified);
    step(&mut app, 4);
    set_mains(&mut app, false);
    step(&mut app, 15);
    app.world_mut().resource_mut::<Modified>().0 = 0;
    let mut levels = vec![];
    for frame in 0..150 {
        set_clock(&mut app, f64::from(frame) * 0.02);
        step(&mut app, 1);
        let mut q = app.world_mut().query::<(&Head, &ChildOf)>();
        levels.extend(q.iter(app.world()).filter(|(_, p)| p.parent() == mast).map(|(h, _)| h.level));
    }
    assert!(levels.iter().any(|l| *l > 0.9) && levels.iter().any(|l| *l < 0.1), "it did breathe");
    assert_eq!(app.world().resource::<Modified>().0, 0, "no material rewritten while breathing");
    let palette = app.world().resource::<traffic::Palette>().len();
    assert!(palette <= 3 * (traffic::LEVEL_STEPS as usize + 1), "a bounded shared palette: {palette}");
}
