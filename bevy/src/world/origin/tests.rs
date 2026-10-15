use super::*;
#[test]
fn udp_sessions_never_request_a_local_procedural_rebase() {
    for mode in ["client", "listen"] {
        let mut world = World::new();
        world.insert_resource(WorldConfig {
            procedural_path: Some("unused-region.json".into()),
            fixed_camera: Some((DVec3::new(4096., 40., 12.), 0., 0.)),
            ..default()
        });
        world.insert_resource(WorldState {
            ready: true,
            ..default()
        });
        world.insert_resource(GameSnapshot(json!({
            "network":{"mode":mode,"ownsWorld":mode=="listen"},
            "player":{"alive":true}
        })));
        world.insert_resource(WorldOrigin::default());
        // No CoreBridge exists: entering the old rebase request path fails.
        automatic_rebase(&mut world);
        assert_eq!(world.resource::<WorldOrigin>().offset, DVec2::ZERO);
        assert_eq!(world.resource::<WorldOrigin>().epoch, 0);
    }
    let mut previous = WorldOrigin {
        offset: DVec2::new(3072., -4096.),
        epoch: 8,
        session_epoch: 3,
    };
    previous.reset_for_network();
    assert_eq!(previous.offset, DVec2::ZERO);
    assert_eq!((previous.epoch, previous.session_epoch), (0, 0));
}
#[test]
fn source_region_floor_and_double_origin_keep_submetre_precision() {
    assert_eq!(threshold_shift(Vec3::new(1024., 1., 0.), 1024.), None);
    assert_eq!(
        threshold_shift(Vec3::new(-1024.25, 1., 18.), 1024.),
        Some(Vec3::new(-1280., 0., 0.))
    );
    let origin = WorldOrigin {
        offset: DVec2::new(1_000_000_000., -1_000_000_000.),
        ..default()
    };
    let absolute = DVec3::new(1_000_000_000.125, 42., -999_999_999.75);
    assert_eq!(origin.local(absolute), Vec3::new(0.125, 42., 0.25));
    assert_eq!(origin.to_absolute(origin.local(absolute)), absolute);
    assert_eq!(origin.shader_offset(), Vec2::new(18944., 13824.));
}

#[test]
fn rebase_moves_render_and_real_rapier_query_without_advancing_time_or_velocity() {
    use bevy::ecs::system::SystemState;
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ));
    app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    app.insert_resource(Time::<Fixed>::from_hz(50.));
    let root = app
        .world_mut()
        .spawn((
            Transform::from_xyz(1030., 2., -1300.),
            RigidBody::Fixed,
            Visibility::Inherited,
        ))
        .id();
    let child = app
        .world_mut()
        .spawn((
            Transform::from_xyz(3., 0., 0.),
            Collider::cuboid(1., 1., 1.),
            ChildOf(root),
        ))
        .id();
    let body = app
        .world_mut()
        .spawn((
            Transform::from_xyz(1040., 10., -1300.),
            RigidBody::Dynamic,
            Collider::ball(0.5),
            Velocity::linear(Vec3::X * 7.),
        ))
        .id();
    let ui = app
        .world_mut()
        .spawn((Transform::from_xyz(2., 3., 4.), Node::default()))
        .id();
    let viewmodel = app
        .world_mut()
        .spawn((Transform::from_xyz(1., 2., 3.), RenderLayers::layer(1)))
        .id();
    let water = app.world_mut().spawn((
        Transform::from_xyz(1024., 25.6, -1536.),
        RenderLayers::layer(17),
        super::super::water::WaterSurface,
    )).id();
    for _ in 0..4 {
        app.update();
    }
    let old_body = app.world().get::<Transform>(body).unwrap().translation;
    let velocity = *app.world().get::<Velocity>(body).unwrap();
    let time = app.world().resource::<Time<Fixed>>().elapsed();
    let shift = Vec3::new(1024., 0., -1536.);
    shift_world(app.world_mut(), shift, shift.xz().as_dvec2());
    assert_eq!(
        app.world().get::<Transform>(water).unwrap().translation,
        Vec3::new(0.,25.6,0.),
    );
    assert_eq!(
        app.world().get::<Transform>(root).unwrap().translation,
        Vec3::new(6., 2., 236.)
    );
    assert_eq!(
        app.world().get::<Transform>(child).unwrap().translation,
        Vec3::X * 3.
    );
    assert_eq!(
        app.world()
            .get::<GlobalTransform>(child)
            .unwrap()
            .translation(),
        Vec3::new(9., 2., 236.)
    );
    assert_eq!(
        app.world().get::<Transform>(body).unwrap().translation,
        old_body - shift
    );
    assert_eq!(app.world().get::<Velocity>(body).unwrap(), &velocity);
    assert_eq!(app.world().resource::<Time<Fixed>>().elapsed(), time);
    assert_eq!(
        app.world().get::<Transform>(ui).unwrap().translation,
        Vec3::new(2., 3., 4.)
    );
    assert_eq!(
        app.world().get::<Transform>(viewmodel).unwrap().translation,
        Vec3::new(1., 2., 3.)
    );
    let mut query = SystemState::<ReadRapierContext>::new(app.world_mut());
    {
        let read = query.get(app.world()).unwrap();
        let context = read.single().unwrap();
        let hit = context.cast_ray(
            Vec3::new(9., 6., 236.),
            Vec3::NEG_Y,
            8.,
            true,
            QueryFilter::default(),
        );
        assert_eq!(
            hit.map(|h| h.0),
            Some(child),
            "pre-physics queries must already use the new frame"
        );
        assert!(
            context
                .cast_ray(
                    Vec3::new(1033., 6., -1300.),
                    Vec3::NEG_Y,
                    8.,
                    true,
                    QueryFilter::default()
                )
                .is_none()
        );
    }
    for _ in 0..3 {
        app.update();
    }
    assert_eq!(
        app.world()
            .get::<GlobalTransform>(child)
            .unwrap()
            .translation(),
        Vec3::new(9., 2., 236.)
    );
    assert!(
        app.world()
            .get::<Transform>(body)
            .unwrap()
            .translation
            .distance(old_body - shift)
            < 2.,
        "writeback must not restore the old origin"
    );
}

#[test]
fn localized_generator_data_keeps_pole_endpoints_but_not_normals_in_local_space() {
    let region:super::super::procedural::ProceduralRegion=serde_json::from_value(json!({
        "cells":1,"spacing":4,"heights":[1,2,3,4],"origin_x":1000000000,"origin_z":-1000000000,
        "trees":[{"x":0.125,"y":5,"z":0.25,"yaw":90,"scale":1,"kind":0,"variant":0}],
        "roads":[{"x0":1000000000.125,"x1":1000000001.25,"z0":-999999999.75,"z1":-999999998.5,"t0X":0.8}],
        "poles":[{"x":1000000000.125,"z":-999999999.75,"nx":1000000128.5,"nz":-999999870.25,"dirX":0.7}],
        "tunnels":[{"x":[1000000000.125],"z":[-999999999.75],"sx":[[1000000002.25]],"sz":[[-999999997.5]]}],
        "tunnelMouths":[[{"x":[1000000001.5],"z":[-999999998.5],"nx":[0.7],"nz":[0.2]}]],
        "pylons":[{"x":1000000000.125,"z":-999999999.75,"wired":[{"x":1000000128.5,"z":-999999870.25,"dirZ":0.8}]}]
    })).unwrap();
    let local = region.localized(DVec2::new(1e9, -1e9));
    assert_eq!(local.patch().unwrap().origin, Vec2::ZERO);
    assert_eq!(local.trees[0].x, 0.125);
    assert_eq!(local.structures["roads"][0]["x0"], 0.125);
    assert_eq!(local.structures["roads"][0]["t0X"], 0.8);
    assert_eq!(local.structures["poles"][0]["nx"], 128.5);
    assert_eq!(local.structures["poles"][0]["nz"], 129.75);
    assert_eq!(local.structures["tunnels"][0]["sx"][0][0], 2.25);
    assert_eq!(local.structures["tunnelMouths"][0][0]["nx"][0], 0.7);
    assert_eq!(local.structures["pylons"][0]["wired"][0]["x"], 128.5);
    assert_eq!(
        region.origin_x, 1e9,
        "cache remains in absolute doubles for later origins"
    );
}

#[test]
fn pending_facts_shift_in_place_without_replaying_consumed_messages() {
    let mut world = World::new();
    let mut messages = Messages::<crate::entities::CoreEvent>::default();
    messages.write(crate::entities::CoreEvent {
        sequence: 7,
        kind: "fire".into(),
        data: json!({"origin":[1025.,4.,8.],"direction":[1.,0.,0.]}),
    });
    let mut used = messages.get_cursor();
    assert_eq!(used.read(&messages).count(), 1);
    let mut pending = messages.get_cursor();
    world.insert_resource(messages);
    shift_world(&mut world, Vec3::X * 1024., DVec2::X * 1024.);
    let messages = world.resource::<Messages<crate::entities::CoreEvent>>();
    assert_eq!(used.read(messages).count(), 0);
    let fact = pending.read(messages).next().unwrap();
    assert_eq!(fact.sequence, 7);
    assert_eq!(fact.data["origin"], json!([1., 4., 8.]));
    assert_eq!(fact.data["direction"], json!([1., 0., 0.]));
}

#[test]
fn queued_teleport_survives_native_barrier_and_next_local_body_publication() {
    let manifest = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let bridge = CoreBridge::launch(manifest)
        .expect("build original core with bevy/run.sh before integration tests");
    let initial=bridge.request("init",json!({"contentRoot":manifest.join("../game/content"),"mapId":"Procedural","spawn":[1025.,40.,12.],"origin":[0.,0.]})).unwrap();
    let mut world = World::new();
    world.insert_resource(bridge);
    world.insert_resource(WorldOrigin::default());
    world.insert_resource(WorldConfig {
        procedural_path: Some("unused-test-region.json".into()),
        fixed_camera: Some((DVec3::new(1025., 40., 12.), 0., 0.)),
        ..default()
    });
    world.insert_resource(WorldState {
        ready: true,
        ..default()
    });
    world.insert_resource(GameSnapshot(initial["state"].clone()));
    world.insert_resource(UiModel::default());
    let body = crate::player::origin_test_player(
        &mut world,
        Vec3::new(1025., 40., 12.),
        initial["state"]["player"]["correctionVersion"]
            .as_u64()
            .unwrap_or(0),
    );
    let pending = world
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"console","args":{"text":"teleport 1030 42 18"}}),
        )
        .unwrap();
    assert_eq!(
        pending["state"]["player"]["correctionVersion"],
        initial["state"]["player"]["correctionVersion"],
        "teleport is still queued when the barrier begins"
    );
    automatic_rebase(&mut world);
    assert_eq!(
        world.resource::<WorldOrigin>().offset,
        DVec2::new(1024., 0.)
    );
    assert_eq!(world.resource::<WorldOrigin>().epoch, 1);
    assert_eq!(
        world.resource::<GameSnapshot>().0["tick"],
        initial["state"]["tick"]
    );
    assert_eq!(
        world.get::<Transform>(body).unwrap().translation,
        Vec3::new(6., 42., 18.)
    );
    let bridge = world.resource::<CoreBridge>();
    let next = bridge
        .request(
            "tick",
            json!({"input":{"position":[6.,42.,18.],"velocity":[0.,0.,0.],"grounded":true}}),
        )
        .unwrap();
    assert_eq!(next["state"]["origin"]["epoch"], 1);
    assert!(
        bridge
            .request(
                "tick",
                json!({"originEpoch":0,"input":{"position":[1025.,40.,12.]}})
            )
            .is_err(),
        "explicit stale buffers must fail rather than be silently retagged"
    );
}
