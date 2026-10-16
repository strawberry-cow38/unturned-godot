//! Production fixed schedule diagnostic for the full-game towing witness.
use super::*;
use crate::{
    player::GameSnapshot,
    vehicle::{self, VehicleBody},
};
use serde_json::json;

fn app(fixed: bool, glazing: bool) -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()));
    if fixed {
        app.add_plugins(
            RapierPhysicsPlugin::<vehicle::PhysicsHooks>::default().in_fixed_schedule(),
        );
    } else {
        app.add_plugins(RapierPhysicsPlugin::<vehicle::PhysicsHooks>::default());
    }
    app.init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<vehicle::collision::Hulls>()
        .init_resource::<super::super::physics::Exceptions>()
        .init_resource::<super::super::recovery::Recoveries>()
        .add_message::<crate::entities::CoreEvent>()
        .insert_resource(GameSnapshot(
            json!({"playerId":1,"network":{"ownsWorld":true}}),
        ))
        .insert_resource(crate::world::WorldState {
            ready: true,
            ..default()
        })
        .insert_resource(Time::<Fixed>::from_hz(50.))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(if fixed { 50 } else { 20 }),
        ))
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .add_systems(
            Update,
            (
                vehicle::collision::update,
                super::super::recovery::apply_adoptions,
                super::super::physics::sync,
            )
                .chain(),
        );
    if fixed {
        app.add_systems(
            FixedUpdate,
            (
                super::super::physics::approach,
                super::super::physics::clamp,
                vehicle::step_bodies,
            )
                .chain()
                .before(PhysicsSet::SyncBackend),
        );
    } else {
        app.add_systems(
            PostUpdate,
            (
                super::super::physics::approach,
                super::super::physics::clamp,
                vehicle::step_bodies,
            )
                .chain()
                .before(PhysicsSet::SyncBackend),
        );
    }
    if glazing {
        app.init_resource::<vehicle::glazing::collision::Shapes>()
            .add_systems(
                Update,
                vehicle::glazing::collision::install
                    .after(vehicle::collision::update)
                    .before(super::super::physics::sync),
            );
    }
    app.update();
    app
}
fn witness(fixed: bool, ccd: bool, retail: bool, glazing: bool, authority: bool) -> f32 {
    let mut app = app(fixed, glazing);
    let cab = super::physics::body(&mut app, "semi", 10, Vec3::new(-601.5, 35., -200.));
    let trailer = super::physics::body(&mut app, "trailer", 11, Vec3::new(-601.5, 35., -190.4));
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(cab).unwrap();
        body.coupling.trailer = 11;
    }
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(trailer).unwrap();
        body.driver_player_id = 0;
        body.coupling.cab = 10;
        body.coupling.driver = 1;
    }
    if ccd {
        for e in [cab, trailer] {
            app.world_mut().entity_mut(e).insert(Ccd::enabled());
        }
    }
    if retail {
        for e in [cab, trailer] {
            let mut b = app.world_mut().get_mut::<VehicleBody>(e).unwrap();
            b.driver_player_id = 0;
            b.coupling = default();
        }
    }
    let ground = if retail {
        let path = std::path::Path::new(
            "/mnt/c/Program Files (x86)/Steam/steamapps/common/Unturned/Maps/PEI/Landscape/Heightmaps/Tile_-1_0_Source.heightmap",
        );
        let patch =
            crate::world::decode_retail_heightmap(&std::fs::read(path).unwrap(), -1, 0).unwrap();
        let data = patch.mesh_by_layer(Vec3::new(-601.5, 33.5, -200.), 100.);
        let mut mesh = crate::content::ObjData::default();
        for layer in data {
            mesh.positions.extend(layer.positions);
        }
        let center = Vec3::new(-601.5, 33.5, -200.);
        let map = path.parent().unwrap().parent().unwrap().parent().unwrap();
        let mut geometry = crate::world::WorldGeometry::default();
        geometry.terrain.push(patch);
        let materials = crate::world::roads::decode_materials(
            &std::fs::read(map.join("Environment/Roads.dat")).unwrap(),
        )
        .unwrap();
        let roads = crate::world::roads::decode_paths(
            &std::fs::read(map.join("Environment/Paths.dat")).unwrap(),
        )
        .unwrap();
        for (index, road) in roads.iter().enumerate() {
            let (_, solid) = crate::world::roads::build(
                road,
                &materials[road.material],
                &geometry,
                center,
                100.,
            );
            if let Some(shape) = crate::world::trimesh_collider(&solid) {
                app.world_mut().spawn((
                    Name::new(format!("Road {index}")),
                    RigidBody::Fixed,
                    shape,
                    Transform::IDENTITY,
                ));
            }
        }
        let root = WorldConfig::default().content_root;
        let names = crate::content::load_guid_map(&root).unwrap();
        let placements = crate::world::parse_placements(
            &std::fs::read_to_string(root.join("objects/placements.txt")).unwrap(),
            &names,
        )
        .unwrap();
        for row in placements.into_iter().filter(|p| {
            p.transform.translation.xz().distance(center.xz()) < 80.
                && !crate::world::is_walk_through(&p.name)
        }) {
            let Ok(mesh) =
                crate::content::ObjData::load(&root.join(format!("objects/{}.obj", row.name)))
            else {
                continue;
            };
            if let Some(shape) = crate::world::trimesh_collider(&mesh) {
                app.world_mut().spawn((
                    Name::new(row.name),
                    RigidBody::Fixed,
                    shape,
                    row.transform,
                ));
            }
        }
        app.world_mut()
            .spawn((
                RigidBody::Fixed,
                crate::world::trimesh_collider(&mesh).unwrap(),
                CollisionGroups::new(Group::GROUP_1, Group::ALL),
                Transform::IDENTITY,
            ))
            .id()
    } else {
        app.world_mut()
            .spawn((
                RigidBody::Fixed,
                Collider::cuboid(100., 0.1, 100.),
                CollisionGroups::new(Group::GROUP_1, Group::ALL),
                Transform::from_xyz(-601.5, 33.197, -200.),
            ))
            .id()
    };
    let frame_scale = if fixed { 2.5 } else { 1. };
    for _ in 0..(150. / frame_scale) as usize {
        app.update();
    }
    if retail {
        if glazing {
            let capture: serde_json::Value = serde_json::from_str(
                &std::fs::read_to_string(
                    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                        .join("artifacts/bevy-coupling-os-entered.json"),
                )
                .unwrap(),
            )
            .unwrap();
            for (entity, id) in [(cab, 1), (trailer, 2)] {
                let row = capture["state"]["vehicles"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .find(|r| r["netId"] == id)
                    .unwrap();
                let row: vehicle::Replica = serde_json::from_value(row.clone()).unwrap();
                super::super::recovery::teleport(
                    app.world_mut(),
                    entity,
                    super::super::recovery::AdoptPose {
                        pose: vehicle::replica_pose(&row),
                        velocity: Velocity {
                            linear: Vec3::from_array(row.lin_vel),
                            angular: Vec3::from_array(row.ang_vel),
                        },
                    },
                );
            }
        }
        let c = *app.world().get::<Transform>(cab).unwrap();
        let mut t = *app.world().get::<Transform>(trailer).unwrap();
        t.translation += c.transform_point(data::named("semi").unwrap().spec.fifth_wheel.into())
            - t.transform_point(data::named("trailer").unwrap().spec.kingpin.into());
        super::super::recovery::teleport(
            app.world_mut(),
            trailer,
            super::super::recovery::AdoptPose {
                pose: t,
                velocity: Velocity::default(),
            },
        );
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(cab).unwrap();
            b.driver_player_id = 1;
            b.coupling.trailer = 11;
        }
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(trailer).unwrap();
            b.coupling.cab = 10;
            b.coupling.driver = 1;
        }
        app.update();
    }
    if authority {
        owner_path(&mut app, cab, trailer);
        app.update();
    }
    let start = app.world().get::<Transform>(cab).unwrap().translation;
    app.world_mut()
        .get_mut::<VehicleBody>(cab)
        .unwrap()
        .controls
        .throttle = 1.;
    for step in 0..(300. / frame_scale) as usize {
        app.update();
        if step % 20 == 0 {
            for entity in [cab, trailer] {
                let pose = app.world().get::<Transform>(entity).unwrap();
                let vel = app.world().get::<Velocity>(entity).unwrap();
                let wheels = &app
                    .world()
                    .get::<RayCastVehicleController>(entity)
                    .unwrap()
                    .wheels;
                eprintln!(
                    "fixed={fixed} ccd={ccd} retail={retail} glazing={glazing} authority={authority} step={step} body={entity:?} at={:?} vel={:?} ground={ground:?} wheels={:?}",
                    pose.translation,
                    vel.linear,
                    wheels
                        .iter()
                        .map(|w| (
                            w.state.ground_entity,
                            w.state.suspension_length,
                            w.engine_force,
                            w.brake
                        ))
                        .collect::<Vec<_>>()
                );
            }
        }
    }
    let end = app.world().get::<Transform>(cab).unwrap().translation;
    eprintln!(
        "fixed={fixed} ccd={ccd} retail={retail} glazing={glazing} authority={authority} start={start:?} end={end:?}"
    );
    start.z - end.z
}
#[test]
fn original_rig_moves_in_main_fixed_schedule_as_well_as_postupdate_fixture() {
    let fixture = witness(false, false, false, false, false);
    let main = witness(true, false, false, false, false);
    assert!(fixture > 3., "fixture moved {fixture}");
    assert!(
        main > 3.,
        "main schedule moved only {main}; fixture={fixture}"
    );
}

#[test]
fn production_ccd_rig_moves_without_phantom_self_contact() {
    let distance = witness(true, true, false, false, false);
    assert!(distance > 3., "main CCD rig moved only {distance}");
}

#[test]
fn production_fixed_ccd_rig_drives_on_actual_pei_road_terrain() {
    let distance = witness(true, true, true, false, false);
    assert!(
        distance > 3.,
        "PEI terrain+road+props rig moved only {distance}"
    );
}

#[test]
fn production_glazing_with_live_entered_pose_drives_on_pei() {
    let distance = witness(true, true, true, true, false);
    assert!(
        distance > 3.,
        "live pose with player colliders moved only {distance}"
    );
}

fn owner_path(app: &mut App, cab: Entity, trailer: Entity) {
    use crate::{
        bridge::CoreBridge,
        ui::{Screen, UiIntent, UiModel, UiState},
    };
    use bevy::input::mouse::AccumulatedMouseMotion;
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let pose = *app.world().get::<Transform>(cab).unwrap();
    bridge.request("init",json!({"contentRoot":WorldConfig::default().content_root,"allowCheats":true,"spawn":pose.translation.to_array()})).unwrap();
    let mut specs = vec![];
    for (key, entity) in [("semi", cab), ("trailer", trailer)] {
        let pose = app.world().get::<Transform>(entity).unwrap();
        let (yaw, pitch, roll) = pose.rotation.to_euler(EulerRot::YXZ);
        let mut spec = vehicle::registration(
            data::named(key).unwrap(),
            key.into(),
            pose.translation,
            yaw.to_degrees(),
            0,
        );
        spec["eulerDegrees"] = json!([pitch.to_degrees(), yaw.to_degrees(), roll.to_degrees()]);
        spec["hasDoors"] = json!(false);
        specs.push(spec);
    }
    let fleet = bridge
        .request("worldVehicles", json!({"vehicles":specs}))
        .unwrap();
    let cab_id = fleet["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
    let trailer_id = fleet["result"]["vehicles"][1]["netId"].as_u64().unwrap() as u32;
    let action = |name: &str, args: serde_json::Value| {
        bridge
            .request("action", json!({"name":name,"args":args}))
            .unwrap();
    };
    let pump = || {
        let mut state = serde_json::Value::Null;
        for _ in 0..12 {
            state = bridge.request("tick", json!({})).unwrap()["state"].clone();
        }
        state
    };
    let trailer_pose = *app.world().get::<Transform>(trailer).unwrap();
    let feet = trailer_pose.transform_point(Vec3::new(2., 0., -3.9));
    action(
        "console",
        json!({"text":format!("teleport {} {} {}",feet.x,feet.y,feet.z)}),
    );
    pump();
    action(
        "vehicleHitch",
        json!({"trailerNetId":trailer_id,"origin":(feet+Vec3::Y*0.7).to_array(),"direction":(trailer_pose.rotation*Vec3::NEG_X).to_array()}),
    );
    let linked = pump();
    assert_eq!(
        linked["vehicles"][0]["coupledTrailerNetId"], trailer_id,
        "{linked}"
    );
    action(
        "console",
        json!({"text":format!("teleport {} {} {}",pose.translation.x,pose.translation.y,pose.translation.z)}),
    );
    pump();
    action("enterVehicle", json!({"netId":cab_id,"seat":0}));
    let state = pump();
    assert_eq!(state["player"]["vehicleNetId"], cab_id, "{state}");
    let mut model = UiModel::default();
    model.adopt_snapshot(&state);
    app.insert_resource(bridge)
        .insert_resource(model)
        .insert_resource(GameSnapshot(state))
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .init_resource::<vehicle::Vehicles>()
        .init_resource::<vehicle::Occupancy>()
        .init_resource::<crate::settings::Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<AccumulatedMouseMotion>()
        .init_resource::<vehicle::turrets::Operator>()
        .add_message::<UiIntent>();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyW);
    for (entity, id) in [(cab, cab_id), (trailer, trailer_id)] {
        let mut body = app.world_mut().get_mut::<VehicleBody>(entity).unwrap();
        body.net_id = id;
        body.coupling = default();
        body.driver_player_id = 0;
        body.flags = 0;
        body.engine = vehicle::audio::Engine::new(1.2, false);
    }
    app.world_mut()
        .resource_mut::<vehicle::Vehicles>()
        .entities
        .extend([(cab_id, cab), (trailer_id, trailer)]);
    app.world_mut().spawn((
        crate::player::PlayerBody::for_motion_test(Vec3::ZERO, false, "stand"),
        Transform::from_translation(pose.translation),
        RigidBody::KinematicPositionBased,
        Collider::capsule(Vec3::Y * 0.35, Vec3::Y * 1.65, 0.35),
        KinematicCharacterController::default(),
        CollisionGroups::new(
            Group::GROUP_2,
            Group::GROUP_1 | Group::GROUP_4 | Group::GROUP_15,
        ),
    ));
    crate::player::test_ready_publisher(app);
    app.add_systems(
        Update,
        (vehicle::sync_vehicles, vehicle::remote::update)
            .chain()
            .before(super::super::recovery::apply_adoptions),
    )
    .add_systems(
        Update,
        (
            vehicle::adopt_occupancy,
            crate::player::sync_seated_body,
            vehicle::read_controls,
        )
            .chain()
            .after(super::super::physics::sync),
    )
    .add_systems(
        FixedUpdate,
        vehicle::publish_bodies
            .after(PhysicsSet::Writeback)
            .in_set(vehicle::VehiclePublish),
    );
}

#[test]
fn actual_owner_read_controls_publish_tick_sync_drives_on_pei() {
    let distance = witness(true, true, true, true, true);
    assert!(distance > 3., "actual owner pipeline moved only {distance}");
}

/// Max |roll| (degrees) and speed of each body after the drop has settled.
fn parked(coupled: bool, trailer_alone: bool) -> (f32, f32) {
    let mut app = app(true, false);
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(100., 0.1, 100.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_xyz(-601.5, 33.197, -200.),
    ));
    let at = Vec3::new(-601.5, 34.5, -200.);
    let cab = (!trailer_alone).then(|| super::physics::body(&mut app, "semi", 10, at));
    let trailer = (coupled || trailer_alone).then(|| {
        let at = if cab.is_some() {
            at + Vec3::from_array(data::named("semi").unwrap().spec.fifth_wheel)
                - Vec3::from_array(data::named("trailer").unwrap().spec.kingpin)
        } else {
            at + Vec3::Z * 10.
        };
        super::physics::body(&mut app, "trailer", 11, at)
    });
    if let (Some(cab), Some(trailer)) = (cab, trailer) {
        app.world_mut().get_mut::<VehicleBody>(cab).unwrap().coupling.trailer = 11;
        let mut b = app.world_mut().get_mut::<VehicleBody>(trailer).unwrap();
        b.driver_player_id = 0;
        b.coupling.cab = 10;
        b.coupling.driver = 0;
    }
    let (mut speed, mut roll) = (0f32, 0f32);
    for step in 0..400 {
        app.update();
        for entity in [cab, trailer].into_iter().flatten() {
            if step >= 200 {
                let pose = app.world().get::<Transform>(entity).unwrap();
                let velocity = app.world().get::<Velocity>(entity).unwrap();
                speed = speed.max(velocity.linear.length());
                roll = roll.max(pose.rotation.to_euler(EulerRot::YXZ).2.to_degrees().abs());
            }
        }
    }
    (roll, speed)
}
#[test]
fn unattended_coupled_rig_settles_level_like_each_body_alone() {
    // The handoff's full-game "oscillation near its start" was this: a
    // compression-reading anti-roll bar rocked the hitched pair to ~24 deg of
    // roll at 3.6 m/s before anyone drove. The source bar reads air gaps only.
    for (coupled, alone, label) in [(false, false, "cab"), (false, true, "trailer"), (true, false, "coupled")] {
        let (roll, speed) = parked(coupled, alone);
        assert!(roll < 1. && speed < 1.5, "{label}: roll {roll} deg, speed {speed} m/s");
    }
}
