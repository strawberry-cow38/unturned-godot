use super::*;
use crate::{content::ContentCache, world::WorldConfig};
use bevy::ecs::system::{RunSystemOnce, SystemState};
use serde_json::json;

mod authority;
mod effects;
mod exits;

fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_resource::<ContentCache>()
    .init_resource::<collision::Shapes>()
    .init_resource::<super::super::collision::Hulls>()
    .init_resource::<WorldConfig>()
    .insert_resource(GameSnapshot(json!({"vehicles":[]})))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(
        Update,
        (super::super::collision::update, collision::install, adopt).chain(),
    );
    app
}
fn build(app: &mut App, key: &'static str, pose: Transform) -> Entity {
    let body = build_unstepped(app, key, pose);
    for _ in 0..3 {
        app.update();
    }
    body
}
fn build_unstepped(app: &mut App, key: &'static str, pose: Transform) -> Entity {
    let body = crate::vehicle::tests::body(app, key, pose.translation);
    app.world_mut().entity_mut(body).insert((
        pose,
        Visibility::Inherited,
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_3),
    ));
    if key == "sedan" {
        app.world_mut()
            .entity_mut(body)
            .insert(super::super::collision::PendingHull);
    }
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
fn ray(
    app: &mut App,
    origin: Vec3,
    direction: Vec3,
    length: f32,
    membership: Group,
) -> Option<(Entity, f32)> {
    let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
    state.get(app.world()).unwrap().single().unwrap().cast_ray(
        origin,
        direction,
        length,
        true,
        QueryFilter::default().groups(CollisionGroups::new(membership, Group::GROUP_4)),
    )
}
fn mask(app: &mut App, broken: u32) {
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"vehicles":[{"netId":1,"glassMask":63,"brokenGlassMask":broken}]});
    for _ in 0..2 {
        app.update();
    }
}

#[test]
fn actual_pane_order_keeps_canonical_holes_and_source_canopy_fallback() {
    let root = WorldConfig::default().content_root;
    let sedan = files("sedan_glass.txt", &root);
    assert_eq!(
        sedan.iter().map(|(i, _)| *i).collect::<Vec<_>>(),
        vec![0, 1, 2, 3, 4, 5]
    );
    assert_eq!(
        files("fighterjet_canopy.txt", &root),
        vec![(17, "fighterjet_canopy.txt".to_owned())]
    );
    let mut app = app();
    let fighter = build(&mut app, "fighterjet", Transform::IDENTITY);
    let glass = app.world().get::<Glazing>(fighter).unwrap();
    assert_eq!(glass.panes.len(), 1);
    let pane = glass.panes[0];
    assert_eq!(app.world().get::<Pane>(pane).unwrap().label(), "canopy");
    assert!(
        app.world().get::<Collider>(pane).is_none(),
        "source fallback never creates a pane body"
    );
    let material = &app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(pane)
        .unwrap()
        .0;
    let material = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(material)
        .unwrap();
    let tint = material.base_color.to_srgba();
    assert!(
        (tint.red - 0.78).abs() < 0.00001
            && (tint.green - 0.62).abs() < 0.00001
            && (tint.alpha - 0.4).abs() < 0.00001,
        "actual fallback uses source golden canopy, {tint:?}"
    );
}

#[test]
fn actual_sedan_break_opens_player_aperture_and_repair_restores_tilted_pane() {
    let mut app = app();
    let pose = Transform::from_xyz(8., 4., -13.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        0.7,
        0.13,
        -0.2,
    ));
    let body = build(&mut app, "sedan", pose);
    assert!(
        !app.world()
            .get::<CollisionGroups>(body)
            .unwrap()
            .filters
            .contains(Group::GROUP_2),
        "the real body mesh, not convex hull, now carries player contact"
    );
    let pane = app
        .world()
        .get::<Glazing>(body)
        .unwrap()
        .panes
        .iter()
        .copied()
        .find(|e| app.world().get::<Pane>(*e).unwrap().label() == "windshield")
        .unwrap();
    let (center, normal) = {
        let p = app.world().get::<Pane>(pane).unwrap();
        let t = app.world().get::<GlobalTransform>(pane).unwrap();
        (t.transform_point(p.center), outward(p, t, pose.translation))
    };
    let origin = center + normal * 0.35;
    assert_eq!(
        ray(&mut app, origin, -normal, 0.7, Group::GROUP_2).map(|x| x.0),
        Some(pane)
    );
    assert_ne!(
        ray(&mut app, origin, -normal, 0.7, Group::GROUP_4).map(|x| x.0),
        Some(pane),
        "driving hulls never collide with their own zero-mass pane"
    );
    assert!(
        app.world().get::<RigidBody>(pane).is_none(),
        "no nested moving-body writeback"
    );
    mask(&mut app, 1);
    assert_eq!(
        *app.world().get::<Visibility>(pane).unwrap(),
        Visibility::Hidden
    );
    assert!(
        ray(&mut app, origin, -normal, 0.7, Group::GROUP_2).is_none(),
        "accepted break is an actual opening, not an invisible convex wall"
    );
    // Negative control: restoring the old chassis mask closes the same opening.
    app.world_mut()
        .get_mut::<CollisionGroups>(body)
        .unwrap()
        .filters
        .insert(Group::GROUP_2);
    for _ in 0..2 {
        app.update();
    }
    assert_eq!(
        ray(&mut app, origin, -normal, 0.7, Group::GROUP_2).map(|x| x.0),
        Some(body)
    );
    app.world_mut()
        .get_mut::<CollisionGroups>(body)
        .unwrap()
        .filters
        .remove(Group::GROUP_2);
    mask(&mut app, 0);
    assert_eq!(
        *app.world().get::<Visibility>(pane).unwrap(),
        Visibility::Inherited
    );
    assert_eq!(
        ray(&mut app, origin, -normal, 0.7, Group::GROUP_2).map(|x| x.0),
        Some(pane)
    );
    let p = app.world().get::<Pane>(pane).unwrap();
    assert!(
        (app.world()
            .get::<GlobalTransform>(pane)
            .unwrap()
            .transform_point(p.center)
            - center)
            .length()
            < 0.0001,
        "tilted hull child shape never writes a world pose back as local"
    );
    app.world_mut().entity_mut(body).despawn();
    app.update();
    assert!(app.world().get_entity(pane).is_err());
    assert!(ray(&mut app, origin, -normal, 0.7, Group::GROUP_2).is_none());
}

#[test]
fn ship_boxes_keep_player_collision_and_source_normal_ties_use_x_then_y() {
    let mut spec = VehicleSpec {
        body: Some("body.txt".into()),
        ..default()
    };
    assert!(body_hitbox(&spec));
    spec.hull_boxes = Some(vec![]);
    assert!(
        !body_hitbox(&spec),
        "even an empty original HullBoxes chooses the source fallback branch"
    );
    let pane = Pane {
        index: 0,
        center: Vec3::new(-2., 0., 0.),
        size: Vec3::ONE,
        debug_color: None,
    };
    let pose = GlobalTransform::from(Transform::from_rotation(Quat::from_rotation_y(0.7)));
    assert!((outward(&pane, &pose, Vec3::ZERO) - pose.rotation() * Vec3::NEG_X).length() < 0.00001);
}

#[test]
fn airborne_plane_ignores_own_player_mesh_and_uses_real_terrain_contact() {
    let mut app = app();
    app.insert_resource(crate::world::WorldState {
        ready: true,
        ..default()
    });
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"network":{"ownsWorld":true}});
    let body = crate::vehicle::tests::body(&mut app, "otter", Vec3::Y * 10.);
    app.world_mut().entity_mut(body).insert((
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_4, Group::ALL & !Group::GROUP_3),
    ));
    // This is the same zero-mass/player-only source triangle installed on the
    // hull. It must participate in player queries, never aircraft ground sensing.
    let pane = app
        .world_mut()
        .spawn((
            Collider::trimesh(
                vec![
                    Vec3::new(-1., -0.1, -1.),
                    Vec3::new(1., -0.1, -1.),
                    Vec3::new(0., -0.1, 1.),
                ],
                vec![[0, 1, 2]],
            )
            .unwrap(),
            Transform::IDENTITY,
            ChildOf(body),
            collision::player_groups(true),
            ColliderMassProperties::Mass(0.),
        ))
        .id();
    for _ in 0..3 {
        app.update();
    }
    let old = app
        .world_mut()
        .run_system_once(move |context: ReadRapierContext| {
            context.single().unwrap().cast_ray(
                Vec3::Y * 10.,
                Vec3::NEG_Y,
                1.,
                true,
                QueryFilter::default().exclude_collider(body),
            )
        })
        .unwrap();
    assert_eq!(
        old.map(|x| x.0),
        Some(pane),
        "negative control reproduces the unfiltered own-floor hit"
    );
    app.world_mut()
        .run_system_once(super::super::step_bodies)
        .unwrap();
    assert!(
        (app.world().get::<Damping>(body).unwrap().angular_damping - 0.1).abs() < 0.00001,
        "an airborne plane keeps source flight damping despite its own pane beneath it"
    );
    app.world_mut().spawn((
        Collider::cuboid(5., 0.1, 5.),
        Transform::from_xyz(0., 9.5, 0.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        crate::world::WorldTerrain,
    ));
    for _ in 0..2 {
        app.update();
    }
    app.world_mut()
        .run_system_once(super::super::step_bodies)
        .unwrap();
    assert!(
        (app.world().get::<Damping>(body).unwrap().angular_damping - 12.1).abs() < 0.00001,
        "the actual terrain still selects original grounded damping"
    );
}

#[test]
fn accepted_glass_masks_open_cabin_audio_without_inventing_open_air_cabins() {
    let mut app = app();
    let sedan = build(&mut app, "sedan", Transform::IDENTITY);
    mask(&mut app, 1);
    assert_eq!(
        app.world()
            .get::<VehicleBody>(sedan)
            .unwrap()
            .cabin_openness(),
        Some(1. / 6.)
    );
    mask(&mut app, 63);
    assert_eq!(
        app.world()
            .get::<VehicleBody>(sedan)
            .unwrap()
            .cabin_openness(),
        Some(1.)
    );
    mask(&mut app, 0);
    assert_eq!(
        app.world()
            .get::<VehicleBody>(sedan)
            .unwrap()
            .cabin_openness(),
        Some(0.)
    );
    let quad = crate::vehicle::tests::body(&mut app, "quad", Vec3::ZERO);
    assert_eq!(
        app.world()
            .get::<VehicleBody>(quad)
            .unwrap()
            .cabin_openness(),
        None
    );
}
