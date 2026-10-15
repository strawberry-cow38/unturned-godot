use super::*;
use crate::world::WorldConfig;
use bevy::ecs::system::RunSystemOnce;

mod visual;
mod seats;

fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        // DefaultPlugins installs this through VisibilityPlugin. The lightweight
        // fixture must retain the same Mesh3d participation in geometry queries.
        .register_required_components::<Mesh3d, Visibility>()
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<StandardMaterial>()
        .init_resource::<ContentCache>()
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .add_systems(Update, animate);
    app
}
fn build(app: &mut App, key: &'static str, at: Vec3) -> Entity {
    let body = super::super::tests::body(app, key, at);
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    app.world_mut()
        .get_mut::<VehicleBody>(body)
        .unwrap()
        .driver_player_id = 0;
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>| {
                super::super::render::build(
                    &mut commands,
                    body,
                    super::super::data::named(key).unwrap(),
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
    body
}
fn named(app: &mut App, name: &str) -> Entity {
    let mut q = app.world_mut().query::<(Entity, &Name)>();
    let rows: Vec<_> = q
        .iter(app.world())
        .filter(|(_, n)| n.as_str() == name)
        .map(|(e, _)| e)
        .collect();
    assert_eq!(rows.len(), 1, "exactly one production part {name}");
    rows[0]
}
fn pose(app: &App, entity: Entity) -> Transform {
    *app.world().get::<Transform>(entity).unwrap()
}
fn ticks(app: &mut App, count: usize) {
    for _ in 0..count {
        app.update();
    }
}

#[test]
fn tank_original_geometry_is_partitioned_once_with_authored_attributes() {
    let mut app = app();
    build(&mut app, "tank", Vec3::ZERO);
    app.update();
    for (file, parts) in [
        (
            "tank_turret.txt",
            vec![
                "tank_turret",
                "tank_mg_box",
                "tank_mg_barrel",
                "tank_top_hatch",
            ],
        ),
        ("tank_hull.txt", vec!["tank_hull.txt", "tank_driver_hatch"]),
    ] {
        let source = app.world().resource::<ContentCache>().geometry
            [&WorldConfig::default().content_root.join(file)]
            .clone();
        let mut count = 0;
        for name in parts {
            let e = named(&mut app, name);
            let handle = &app.world().get::<Mesh3d>(e).unwrap().0;
            let mesh = app.world().resource::<Assets<Mesh>>().get(handle).unwrap();
            let points = mesh
                .attribute(Mesh::ATTRIBUTE_POSITION)
                .unwrap()
                .as_float3()
                .unwrap();
            assert!(!points.is_empty(), "real source partition {name} exists");
            for attribute in [
                Mesh::ATTRIBUTE_NORMAL,
                Mesh::ATTRIBUTE_UV_0,
                Mesh::ATTRIBUTE_COLOR,
            ] {
                assert_eq!(mesh.attribute(attribute).unwrap().len(), points.len());
            }
            count += points.len();
        }
        assert_eq!(
            count,
            source.positions.len(),
            "{file}: no doubled lid or lost original triangles"
        );
    }
    let cap = named(&mut app, "tank_driver_hatch_cap");
    let mesh = app
        .world()
        .resource::<Assets<Mesh>>()
        .get(&app.world().get::<Mesh3d>(cap).unwrap().0)
        .unwrap();
    assert_eq!(
        mesh.count_vertices(),
        12,
        "original missing hinge face has both windings"
    );
    named(&mut app, "tank_treads.txt");
}

#[test]
fn tank_roof_gun_inherits_turret_yaw_but_never_cannon_elevation() {
    let mut app = app();
    let tank = build(&mut app, "tank", Vec3::new(12., 4., -8.));
    app.world_mut().get_mut::<Transform>(tank).unwrap().rotation =
        Quat::from_rotation_y(37_f32.to_radians());
    app.world_mut().get_mut::<Mounts>(tank).unwrap().angles =
        vec![Vec2::new(90., 30.), Vec2::new(-25., 15.)];
    app.update();
    let mounts = app.world().get::<Mounts>(tank).unwrap();
    let main = mounts.frames[0].pitch;
    let roof = mounts.frames[1].pitch;
    let actual = app.world().get::<GlobalTransform>(roof).unwrap();
    let yaw = Quat::from_rotation_y(127_f32.to_radians());
    let expected_rotation =
        Quat::from_rotation_y(102_f32.to_radians()) * Quat::from_rotation_x(15_f32.to_radians());
    assert!(actual.rotation().angle_between(expected_rotation).abs() < 0.0001);
    let def = super::super::data::named("tank")
        .unwrap()
        .spec
        .turrets
        .as_ref()
        .unwrap();
    let expected_origin = pose(&app, tank).transform_point(Vec3::from_array(def[0].pivot))
        + yaw * Vec3::from_array(def[1].pivot);
    assert!(actual.translation().distance(expected_origin) < 0.0001);
    let muzzle_before = actual.transform_point(Vec3::from_array(def[1].muzzle));
    app.world_mut().get_mut::<Mounts>(tank).unwrap().angles[0].y = -10.;
    app.update();
    let after = app
        .world()
        .get::<GlobalTransform>(roof)
        .unwrap()
        .transform_point(Vec3::from_array(def[1].muzzle));
    assert!(after.distance(muzzle_before) < 0.0001);
    assert!(
        pose(&app, main)
            .rotation
            .angle_between(Quat::from_rotation_x(-10_f32.to_radians()))
            .abs()
            < 0.0001
    );
}

#[test]
fn tank_hatches_follow_accepted_seats_and_original_curves() {
    let mut app = app();
    let tank = build(&mut app, "tank", Vec3::ZERO);
    app.update();
    let top = named(&mut app, "Tank turret hatch");
    let driver = named(&mut app, "Tank driver hatch");
    assert!(pose(&app, top).rotation.angle_between(Quat::IDENTITY).abs() < 0.0001);
    {
        let mut body = app.world_mut().get_mut::<VehicleBody>(tank).unwrap();
        body.driver_player_id = 10;
        body.passengers = vec![20];
    }
    ticks(&mut app, 20);
    // At0.4s, the original close clip is already92.5035% shut: linear
    // rotation or an accidentally reversed clip is visibly wrong here.
    let half = pose(&app, top)
        .rotation
        .to_euler(EulerRot::YXZ)
        .1
        .to_degrees();
    assert!(
        pose(&app, top)
            .rotation
            .angle_between(Quat::from_rotation_x(-92.5035_f32.to_radians()))
            .abs()
            < 0.001,
        "original close easing, actual{half}"
    );
    ticks(&mut app, 22);
    assert!(
        pose(&app, top)
            .rotation
            .angle_between(Quat::from_rotation_x(-100_f32.to_radians()))
            .abs()
            < 0.0001
    );
    assert!(
        pose(&app, driver)
            .rotation
            .abs_diff_eq(Quat::from_rotation_x(-50_f32.to_radians()), 0.000001)
    );
    let expected = Vec3::new(0., 1.88, -3.4) + Vec3::Y * 0.1 + Vec3::new(0., -0.447, -0.894) * 0.22;
    assert!(pose(&app, driver).translation.distance(expected) < 0.0001);
    app.world_mut().get_mut::<Mounts>(tank).unwrap().head_out = true;
    ticks(&mut app, 42);
    assert!(pose(&app, top).rotation.angle_between(Quat::IDENTITY).abs() < 0.0001);
    assert!(
        pose(&app, driver)
            .rotation
            .abs_diff_eq(Quat::from_rotation_x(-50_f32.to_radians()), 0.000001)
    );
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .passengers
        .clear();
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .driver_player_id = 0;
    ticks(&mut app, 42);
    assert!(!app.world().get::<Mounts>(tank).unwrap().head_out);
    assert!(
        pose(&app, driver)
            .rotation
            .angle_between(Quat::IDENTITY)
            .abs()
            < 0.0001
    );
}

#[test]
fn local_pause_freezes_hatches_but_multiplayer_menu_does_not() {
    let mut app = app();
    let tank = build(&mut app, "tank", Vec3::ZERO);
    app.update();
    app.world_mut()
        .get_mut::<VehicleBody>(tank)
        .unwrap()
        .driver_player_id = 10;
    app.insert_resource(crate::ui::UiState {
        screen: crate::ui::Screen::Paused,
        ..default()
    });
    ticks(&mut app, 20);
    assert_eq!(
        app.world()
            .get::<Mounts>(tank)
            .unwrap()
            .hatches
            .as_ref()
            .unwrap()
            .driver_phase,
        1.
    );
    app.world_mut()
        .resource_mut::<crate::ui::UiState>()
        .networked = true;
    ticks(&mut app, 42);
    assert_eq!(
        app.world()
            .get::<Mounts>(tank)
            .unwrap()
            .hatches
            .as_ref()
            .unwrap()
            .driver_phase,
        0.
    );
}

#[test]
fn original_helicopter_mounts_have_chin_mesh_and_crew_gated_door_guns() {
    for key in ["hind", "huey", "orca"] {
        let mut app = app();
        let heli = build(&mut app, key, Vec3::ZERO);
        app.update();
        let defs = super::super::data::named(key)
            .unwrap()
            .spec
            .turrets
            .as_ref()
            .unwrap();
        let guns: Vec<_> = app
            .world()
            .get::<Mounts>(heli)
            .unwrap()
            .frames
            .iter()
            .map(|f| f.gun.unwrap())
            .collect();
        assert_eq!(guns.len(), defs.len());
        for (i, e) in guns.iter().enumerate() {
            assert_eq!(
                *app.world().get::<Visibility>(*e).unwrap(),
                if key == "hind" {
                    Visibility::Inherited
                } else {
                    Visibility::Hidden
                }
            );
            let expected = if key == "hind" {
                Quat::IDENTITY
            } else {
                Quat::from_rotation_x(-std::f32::consts::FRAC_PI_2)
            };
            assert!(
                pose(&app, *e).rotation.abs_diff_eq(expected, 0.000001),
                "{key}: {:?} vs {expected:?}",
                pose(&app, *e).rotation
            );
            app.world_mut().get_mut::<Mounts>(heli).unwrap().crew_alive[i] = true;
        }
        app.update();
        assert!(
            guns.iter()
                .all(|e| *app.world().get::<Visibility>(*e).unwrap() == Visibility::Inherited)
        );
    }
}

#[test]
fn moving_barrel_player_collision_is_separate_from_hull_dynamics() {
    use bevy::ecs::system::SystemState;
    let mut app = app();
    app.add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        });
    let tank = build(&mut app, "tank", Vec3::ZERO);
    app.world_mut().entity_mut(tank).insert((
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    let barrel = named(&mut app, "Tank barrel collider");
    ticks(&mut app, 3);
    let probe = |app: &mut App, at: Vec3, dir: Vec3, group: Group| {
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let read = state.get(app.world()).unwrap();
        let ctx = read.single().unwrap();
        ctx.cast_ray(
            at,
            dir,
            2.,
            true,
            QueryFilter::default().groups(CollisionGroups::new(group, Group::GROUP_4)),
        )
    };
    assert_eq!(
        probe(&mut app, Vec3::new(-1., 2.8, -5.5), Vec3::X, Group::GROUP_2).map(|x| x.0),
        Some(barrel)
    );
    assert!(
        probe(&mut app, Vec3::new(-1., 2.8, -5.5), Vec3::X, Group::GROUP_4).is_none(),
        "vehicle hull cannot collide with its own moving barrel"
    );
    assert_ne!(
        app.world().get::<RapierRigidBodyHandle>(tank).unwrap().0,
        app.world().get::<RapierRigidBodyHandle>(barrel).unwrap().0,
        "articulation must not reinsert shapes into hull mass/suspension body"
    );
    app.world_mut().get_mut::<Mounts>(tank).unwrap().angles[0].x = 90.;
    ticks(&mut app, 3);
    assert!(probe(&mut app, Vec3::new(-1., 2.8, -5.5), Vec3::X, Group::GROUP_2).is_none());
    assert_eq!(
        probe(
            &mut app,
            Vec3::new(-6., 2.8, -0.15),
            Vec3::Z,
            Group::GROUP_2
        )
        .map(|x| x.0),
        Some(barrel)
    );
}

#[test]
fn authority_rays_follow_articulated_meshes_when_stationary_hull_does_not_move() {
    use crate::{
        bridge::CoreBridge,
        player::{GameSnapshot, WorldSessionEpoch},
        ui::UiModel,
        world::{WorldGeometry, WorldState},
    };
    use serde_json::json;
    let mut app = app();
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let reply = bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[20,0,20]}),
        )
        .unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&reply["state"]);
    assert!(model.connected && model.network.owns_world);
    app.insert_resource(bridge)
        .insert_resource(model)
        .insert_resource(config)
        .insert_resource(GameSnapshot(reply["state"].clone()))
        .insert_resource(WorldState {
            ready: true,
            ..default()
        })
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<WorldGeometry>()
        .init_resource::<super::super::geometry::GeometryUploads>()
        .add_systems(
            PostUpdate,
            super::super::geometry::upload.after(TransformSystems::Propagate),
        );
    let tank = build(&mut app, "tank", Vec3::ZERO);
    let registered = app
        .world()
        .resource::<CoreBridge>()
        .request(
            "worldVehicles",
            json!({"replace":false,"vehicles":[super::super::registration(
            super::super::data::named("tank").unwrap(), "mount-test".into(), Vec3::ZERO, 0., 0)]}),
        )
        .unwrap();
    let id = registered["result"]["vehicles"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    app.world_mut().get_mut::<VehicleBody>(tank).unwrap().net_id = id;
    ticks(&mut app, 2);
    let ray = |app: &App, from: Vec3, to: Vec3| {
        app.world()
            .resource::<CoreBridge>()
            .request(
                "worldRay",
                json!({"from":from.to_array(),"to":to.to_array()}),
            )
            .unwrap()["result"]
            .clone()
    };
    let old_from = Vec3::new(-1., 2.8, -5.5);
    let old_to = Vec3::new(1., 2.8, -5.5);
    assert_eq!(
        ray(&app, old_from, old_to)["hit"],
        true,
        "real forward cannon triangles block accepted bullets"
    );
    app.world_mut().get_mut::<Mounts>(tank).unwrap().angles[0].x = 90.;
    ticks(&mut app, 2);
    assert_eq!(
        ray(&app, old_from, old_to)["hit"],
        false,
        "unchanged hull must still republish newly rotated mount"
    );
    assert_eq!(
        ray(&app, Vec3::new(-6., 2.8, -0.15), Vec3::new(-6., 2.8, 1.85))["hit"],
        true
    );
    app.world_mut().despawn(tank);
    app.update();
    assert_eq!(
        ray(&app, Vec3::new(-6., 2.8, -0.15), Vec3::new(-6., 2.8, 1.85))["hit"],
        false,
        "removed vehicle retires all child geometry"
    );
}

#[test]
fn original_fleet_mounts_preserve_weapon_seats_parent_frames_and_retail_pivots() {
    let fleet = &*super::super::data::FLEET;
    let armed: Vec<_> = fleet
        .iter()
        .filter(|v| v.spec.turrets.as_ref().is_some_and(|t| !t.is_empty()))
        .map(|v| v.key.as_str())
        .collect();
    assert_eq!(armed, vec!["huey", "hind", "orca", "tank"]);
    let hind = super::super::data::named("hind")
        .unwrap()
        .spec
        .turrets
        .as_ref()
        .unwrap();
    assert_eq!(
        (hind[0].seat, hind[0].gun_id.as_str(), hind[0].belt),
        (1, "gsh23", 450)
    );
    assert_eq!(hind[0].pivot, [0., 0.064, -4.378]);
    let tank = super::super::data::named("tank")
        .unwrap()
        .spec
        .turrets
        .as_ref()
        .unwrap();
    assert_eq!(
        (tank[0].gun_id.as_str(), tank[1].gun_id.as_str()),
        ("tank_cannon", "hmg")
    );
    assert_eq!(tank[0].pitch_offset, [0., 2.8, -2.]);
    assert_eq!(tank[1].parent_mount, 0);
    assert!(tank[0].driver_fallback && tank[1].driver_fallback);
    assert!(tank[0].hold_to_aim && !tank[1].hold_to_aim);
}
