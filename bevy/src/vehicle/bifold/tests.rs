use super::*;
use crate::{content::ObjData, vehicle::data, world::WorldConfig};
use bevy::ecs::system::RunSystemOnce;
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;
mod accepted;
mod visual;

fn original() -> (ObjData, geometry::Parts) {
    let spec = &data::named("bus").unwrap().spec;
    let source = ObjData::load(
        &WorldConfig::default()
            .content_root
            .join(spec.body.as_ref().unwrap()),
    )
    .unwrap();
    let parts = geometry::cut(&source, spec).unwrap();
    (source, parts)
}
fn shape(data: &ObjData) -> Collider {
    Collider::trimesh(
        data.positions
            .iter()
            .map(|p| Vec3::from_array(*p))
            .collect(),
        (0..data.positions.len() as u32)
            .collect::<Vec<_>>()
            .chunks_exact(3)
            .map(|p| [p[0], p[1], p[2]])
            .collect(),
    )
    .unwrap()
}
#[test]
fn original_bus_leaves_trim_to_doorway_and_cap_every_open_cut_with_source_palette() {
    let (_, parts) = original();
    let a = &parts.leaf_a;
    let b = parts.leaf_b.as_ref().unwrap();
    for leaf in [a, b] {
        assert!((leaf.min.y + 0.12).abs() < 1e-5);
        assert!(leaf.max.y > 1.9);
        assert!((leaf.max.z - leaf.min.z - 0.5).abs() < 1e-5);
        assert!((leaf.max.x - leaf.min.x - 0.125).abs() < 0.001);
        for (axis, value, normal) in [
            (2, leaf.min.z, -Vec3::Z),
            (2, leaf.max.z, Vec3::Z),
            (1, -0.12, -Vec3::Y),
        ] {
            let cap = leaf
                .positions
                .chunks_exact(3)
                .enumerate()
                .find(|(t, p)| {
                    p.iter().all(|p| (p[axis] - value).abs() < 1e-5)
                        && leaf.normals[t * 3..t * 3 + 3]
                            .iter()
                            .all(|n| Vec3::from_array(*n).dot(normal) > 0.999)
                })
                .expect("original trim/mullion/bottom cap must exist");
            let i = cap.0 * 3;
            assert!(Vec2::from(leaf.uvs[i]).distance(Vec2::from(leaf.uvs[i + 1])) < 1e-6);
            assert!(Vec2::from(leaf.uvs[i]).distance(Vec2::from(leaf.uvs[i + 2])) < 1e-6);
            assert_eq!(leaf.colors[i], leaf.colors[i + 2]);
        }
    }
    assert!((a.min.z + 3.25).abs() < 1e-5);
    assert!((a.max.z + 2.75).abs() < 1e-5);
    assert!((b.min.z + 2.75).abs() < 1e-5);
    assert!((b.max.z + 2.25).abs() < 1e-5);
}
#[test]
fn original_floor_pocket_replaces_upper_step_and_the_body_no_longer_contains_the_door_panel() {
    let (whole, parts) = original();
    let old = shape(&whole);
    let current = shape(&parts.body);
    let ray = |shape: &Collider, position, direction| {
        shape.raw.cast_local_ray(
            &bevy_rapier3d::rapier::parry::query::Ray::new(position, direction),
            10.,
            true,
        )
    };
    let probe = Vec3::new(0.9, 0.4, -2.9);
    let old_floor = ray(&old, probe, -Vec3::Y).unwrap();
    let new_floor = ray(&current, probe, -Vec3::Y).unwrap();
    assert!(
        (old_floor - 0.275).abs() < 1e-4,
        "original cabin floor at+.125: {old_floor}"
    );
    assert!(
        (new_floor - 0.525).abs() < 1e-4,
        "source pocket floor at-.125: {new_floor}"
    );
    let door_probe = Vec3::new(2., 0.25, -2.9);
    let old_door = ray(&old, door_probe, -Vec3::X).unwrap();
    let new_door = ray(&current, door_probe, -Vec3::X).unwrap();
    assert!(old_door < 0.7, "old door panel blocks this crossing");
    assert!(new_door > 1., "body no longer duplicates moving panel");
}
#[test]
fn source_exclusive_body_branch_never_applies_a_door_split_over_aircraft_or_light_zones() {
    let spec = &data::named("bus").unwrap().spec;
    assert_eq!(
        spec.door_hinge_bx, 1.4185,
        "retain the source's uppercase BX field rather than silently taking the fallback hinge"
    );
    assert!(geometry::applies(spec));
    for mode in 0..4 {
        let mut modified = spec.clone();
        match mode {
            0 => modified.heli = true,
            1 => modified.plane = true,
            2 => modified.headlight_zone_max = [1.; 3],
            _ => modified.landing_leg_zone_max = [1.; 3],
        }
        assert!(!geometry::applies(&modified));
    }
}

fn app() -> App {
    use crate::{content::ContentCache, player::GameSnapshot};
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_resource::<ContentCache>()
    .init_resource::<super::super::glazing::collision::Shapes>()
    .insert_resource(GameSnapshot(serde_json::json!({"vehicles":[]})))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(
        Update,
        (
            super::super::glazing::collision::install,
            super::super::glazing::adopt,
            adopt,
        )
            .chain(),
    );
    app
}
fn bus(app: &mut App, hull: Transform) -> Entity {
    use crate::content::ContentCache;
    let body = crate::vehicle::tests::body(app, "bus", hull.translation);
    app.world_mut().entity_mut(body).insert((
        hull,
        Visibility::Inherited,
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  assets: Res<AssetServer>,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>| {
                super::super::render::build(
                    &mut commands,
                    body,
                    data::named("bus").unwrap(),
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

#[test]
fn production_bus_panes_follow_nested_hinges_and_accepted_repair_keeps_player_aperture() {
    let mut app = app();
    let hull = Transform::from_xyz(7., 3., -8.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        0.4,
        0.15,
        -0.12,
    ));
    let body = bus(&mut app, hull);
    for _ in 0..3 {
        app.update();
    }
    let panes = app
        .world()
        .get::<super::super::glazing::Glazing>(body)
        .unwrap()
        .panes
        .clone();
    let (a, b) = {
        let door = app.world().get::<Built>(body).unwrap();
        (door.a, door.b.unwrap())
    };
    let attached: Vec<_> = panes
        .iter()
        .filter_map(|e| app.world().get::<DoorPane>(*e).map(|_| *e))
        .collect();
    assert_eq!(attached.len(), 2);
    assert!(
        app.world()
            .get::<CollisionGroups>(body)
            .unwrap()
            .filters
            .intersection(Group::GROUP_2)
            .is_empty()
    );
    for &pane in &attached {
        let local = app
            .world()
            .get::<super::super::glazing::Pane>(pane)
            .unwrap();
        let parent = app.world().get::<ChildOf>(pane).unwrap().parent();
        assert_eq!(parent, if local.label() == "r_front" { a } else { b });
        assert_eq!(
            app.world().get::<CollisionGroups>(pane).unwrap().filters,
            Group::GROUP_4
        );
    }
    let crossing = hull.transform_point(Vec3::new(2., 1., -2.5));
    let direction = hull.rotation * -Vec3::X;
    let ray = |app: &mut App, membership: Group| {
        let mut state = bevy::ecs::system::SystemState::<ReadRapierContext>::new(app.world_mut());
        state.get(app.world()).unwrap().single().unwrap().cast_ray(
            crossing,
            direction,
            1.3,
            true,
            QueryFilter::default()
                .groups(CollisionGroups::new(membership, Group::GROUP_4))
                .predicate(&|entity, _| entity != body),
        )
    };
    assert!(
        ray(&mut app, Group::GROUP_2).is_none(),
        "closed door/panes never block the source player aperture"
    );
    assert!(
        ray(&mut app, Group::GROUP_4).is_some(),
        "the same closed leaf still blocks other vehicles"
    );
    let masks = [(0.25_f32, true, 1 << 3), (1., true, 0), (0., false, 0)];
    for (phase, open, broken) in masks {
        app.world_mut().resource_mut::<GameSnapshot>().0 = serde_json::json!({"vehicles":[{"netId":1,"doorFold":phase,"doorOpen":open,"glassMask":(1<<17)-1,"brokenGlassMask":broken}]});
        app.update();
        assert!(
            ray(&mut app, Group::GROUP_2).is_none(),
            "moving and repaired panes must keep the player aperture"
        );
        let e = if phase < 0.5 {
            2. * phase * phase
        } else {
            1. - (-2. * phase + 2.).powi(2) * 0.5
        };
        let pose_a = Transform::from_xyz(1.293, 0., -3.25)
            .with_rotation(Quat::from_rotation_y(-std::f32::consts::FRAC_PI_2 * e));
        let pose_b = Transform::from_xyz(1.4185 - 1.293, 0., 0.5)
            .with_rotation(Quat::from_rotation_y(std::f32::consts::PI * e));
        for &pane in &attached {
            let data = app
                .world()
                .get::<super::super::glazing::Pane>(pane)
                .unwrap();
            let leaf_a = data.label() == "r_front";
            let offset = Transform::from_translation(if leaf_a {
                Vec3::new(-1.293, 0., 3.25)
            } else {
                Vec3::new(-1.4185, 0., 2.75)
            });
            let expected = if leaf_a {
                hull.to_matrix() * pose_a.to_matrix() * offset.to_matrix()
            } else {
                hull.to_matrix() * pose_a.to_matrix() * pose_b.to_matrix() * offset.to_matrix()
            };
            let actual = app
                .world()
                .get::<GlobalTransform>(pane)
                .unwrap()
                .to_matrix();
            assert!(
                actual.abs_diff_eq(expected, 1e-5),
                "{} at phase{phase}: actual{actual:?}, expected{expected:?}",
                data.label()
            );
            assert_eq!(
                app.world().get::<CollisionGroups>(pane).unwrap().filters,
                Group::GROUP_4,
                "repair must not put source player collision back on the door"
            );
            assert_eq!(
                app.world()
                    .get::<CollisionGroups>(pane)
                    .unwrap()
                    .memberships,
                if broken & (1 << data.index) != 0 {
                    Group::NONE
                } else {
                    Group::GROUP_4
                }
            );
        }
    }
    app.world_mut().despawn(body);
    for pane in attached {
        assert!(app.world().get_entity(pane).is_err());
    }
    assert!(app.world().get_entity(a).is_err());
    assert!(app.world().get_entity(b).is_err());
}

#[test]
fn native_source_leaf_boxes_reach_authority_rays_and_stationary_fold_replaces_them() {
    use crate::{
        bridge::CoreBridge,
        player::WorldSessionEpoch,
        ui::UiModel,
        vehicle::geometry::{GeometryUploads, upload},
        world::{WorldGeometry, WorldState},
    };
    use serde_json::json;
    let mut app = app();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let config = WorldConfig::default();
    bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[0,0,0]}),
        )
        .unwrap();
    let definition = data::named("bus").unwrap();
    let id = bridge.request("worldVehicles", json!({"vehicles":[crate::vehicle::registration(definition,"bus door geometry".into(),Vec3::ZERO,0.,0)]})).unwrap()["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
    let mut model = UiModel::default();
    model.connected = true;
    model.network.owns_world = true;
    app.insert_resource(bridge)
        .insert_resource(model)
        .insert_resource(config)
        .insert_resource(WorldState {
            ready: true,
            ..default()
        })
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<WorldGeometry>()
        .init_resource::<GeometryUploads>();
    let body = bus(&mut app, Transform::IDENTITY);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    // This fixture tests geometry adoption, not the subsequent accepted toggle
    // command. Hide glass so the source full DoorHit must cover a visual window.
    let phase = |app: &mut App, fold: f32| {
        app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"vehicles":[{"netId":id,"doorFold":fold,"doorOpen":fold>0.,"glassMask":131071,"brokenGlassMask":131071}]});
        app.update();
        app.update();
        app.world_mut().run_system_once(upload).unwrap();
    };
    let ray = |app: &App| {
        app.world()
            .resource::<CoreBridge>()
            .request("worldRay", json!({"from":[2.,1.5,-2.5],"to":[1.,1.5,-2.5]}))
            .unwrap()["result"]
            .clone()
    };
    phase(&mut app, 0.);
    let closed = ray(&app);
    assert_eq!(
        closed["hit"], true,
        "source full leaf box must block its visual window: {closed}"
    );
    assert_eq!(closed["vehicleNetId"], id);
    assert_eq!(closed["bodyPart"], "hull");
    // A stationary bus must still replace authority geometry after folding.
    phase(&mut app, 1.);
    assert_eq!(ray(&app)["hit"], false);
    phase(&mut app, 0.);
    assert_eq!(ray(&app)["hit"], true);
    // Removing only the box-upload marker reproduces the incomplete visual-only
    // port on the same real mesh; this window then has no authority obstacle.
    let leaves = app
        .world_mut()
        .query_filtered::<Entity, With<Leaf>>()
        .iter(app.world())
        .collect::<Vec<_>>();
    for leaf in leaves {
        app.world_mut().entity_mut(leaf).remove::<Leaf>();
    }
    app.insert_resource(GeometryUploads::default());
    app.world_mut().run_system_once(upload).unwrap();
    assert_eq!(
        ray(&app)["hit"],
        false,
        "negative control must hit an actual hollow window, not a solid part of the panel"
    );
}
