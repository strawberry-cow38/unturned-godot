use super::hull_mesh::*;
use crate::content::{ContentCache, ObjData};
use crate::vehicle::data;
use crate::world::WorldConfig;
use bevy::ecs::system::{RunSystemOnce, SystemState};
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;

pub(super) fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        RapierPhysicsPlugin::<Hooks>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_resource::<ContentCache>()
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(TimestepMode::Fixed {
        dt: 0.02,
        substeps: 1,
    });
    app
}
pub(super) fn ship(app: &mut App) -> (Entity, Entity) {
    let owner = crate::vehicle::tests::body(app, "ship", Vec3::ZERO);
    app.world_mut().entity_mut(owner).insert((
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_4, Group::ALL),
    ));
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>| {
                crate::vehicle::render::build(
                    &mut commands,
                    owner,
                    data::named("ship").unwrap(),
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
    app.update();
    app.update();
    let hull = app
        .world_mut()
        .query_filtered::<Entity, With<HullMesh>>()
        .single(app.world())
        .unwrap();
    (owner, hull)
}
fn roof(app: &App, hull: Entity) -> (Vec3, Vec3) {
    let mesh = app
        .world()
        .get::<Collider>(hull)
        .unwrap()
        .as_trimesh()
        .unwrap();
    let triangle = mesh
        .raw
        .triangles()
        .filter(|t| {
            let n = (t.b - t.a).cross(t.c - t.a).normalize_or_zero();
            n.y.abs() > 0.99 && t.a.y > 20.
        })
        .max_by(|a, b| a.area().total_cmp(&b.area()))
        .unwrap();
    ((triangle.a + triangle.b + triangle.c) / 3., Vec3::Y)
}
#[test]
fn original_ship_region_installs_exact_world_surface_and_follows_its_parent() {
    let mut app = app();
    let (owner, hull) = ship(&mut app);
    let d = data::named("ship").unwrap();
    let mut original = ObjData::load_source_faces(
        &WorldConfig::default()
            .content_root
            .join(d.spec.body.as_ref().unwrap()),
    )
    .unwrap();
    for vertex in &mut original.positions {
        *vertex = super::source_face_vertex(*vertex).to_array();
    }
    let (lo, hi) = d.spec.hull_trimesh.unwrap();
    let lo = Vec3::from_array(lo);
    let hi = Vec3::from_array(hi);
    let inside = |p: Vec3| p.cmpge(lo).all() && p.cmple(hi).all();
    let corners = original
        .positions
        .chunks_exact(3)
        .filter(|tri| tri.iter().all(|p| inside(Vec3::from_array(*p))))
        .count();
    let centroids = original
        .positions
        .chunks_exact(3)
        .filter(|tri| inside(tri.iter().map(|p| Vec3::from_array(*p)).sum::<Vec3>() / 3.))
        .count();
    assert!(
        centroids > corners,
        "real original deck triangles exercise the centroid negative control"
    );
    assert_eq!(
        app.world()
            .get::<Collider>(hull)
            .unwrap()
            .as_trimesh()
            .unwrap()
            .raw
            .num_triangles(),
        corners
    );
    assert_eq!(app.world().get::<RigidBody>(hull), Some(&RigidBody::Fixed));
    let groups = app.world().get::<CollisionGroups>(hull).unwrap();
    assert_eq!(groups.memberships, Group::GROUP_1);
    let (center, normal) = roof(&app, hull);
    let ray = |app: &mut App, origin: Vec3, dir: Vec3| {
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        state.get(app.world()).unwrap().single().unwrap().cast_ray(
            origin,
            dir,
            2.,
            true,
            QueryFilter::default().groups(CollisionGroups::new(Group::GROUP_2, Group::GROUP_1)),
        )
    };
    assert_eq!(ray(&mut app, center + normal, -normal).unwrap().0, hull);
    assert_eq!(
        ray(&mut app, center - normal, normal).unwrap().0,
        hull,
        "surface is queryable from both sides"
    );
    let pose = Transform::from_xyz(30., 4., -12.).with_rotation(Quat::from_rotation_y(0.6));
    app.world_mut().entity_mut(owner).insert(pose);
    crate::world::origin::move_physics_owner(app.world_mut(), owner, pose.translation);
    app.update();
    app.update();
    let actual = app.world().get::<Transform>(owner).unwrap();
    assert!(
        (actual.translation - pose.translation).length() < 0.0001,
        "the actual carrier must move before testing its attached surface: {actual:?}"
    );
    assert!(
        ray(&mut app, center + normal, -normal).is_none(),
        "old world surface retires"
    );
    let point = pose.transform_point(center);
    let normal = pose.rotation * normal;
    assert_eq!(
        ray(&mut app, point + normal, -normal).unwrap().0,
        hull,
        "moving carrier updates independent static body"
    );
    app.world_mut().entity_mut(owner).despawn();
    app.update();
    assert!(app.world().get_entity(hull).is_err());
    assert!(ray(&mut app, point + normal, -normal).is_none());
}
#[test]
fn independent_hull_surface_blocks_a_real_body_without_pushing_its_own_carrier() {
    let mut app = app();
    let (owner, hull) = ship(&mut app);
    let (roof, _) = roof(&app, hull);
    // Deliberate carrier overlap makes the source own-body exception observable.
    // It must not turn into a mass-zero attached shape that transmits impulses.
    app.world_mut().entity_mut(owner).insert((
        RigidBody::Dynamic,
        GravityScale(0.),
        Collider::cuboid(12., 24., 30.),
        ColliderMassProperties::Mass(1000.),
    ));
    let visitor = app
        .world_mut()
        .spawn((
            RigidBody::Dynamic,
            Collider::ball(0.25),
            ColliderMassProperties::Mass(100.),
            Transform::from_translation(roof + Vec3::Y * 1.5),
            CollisionGroups::new(Group::GROUP_2, Group::GROUP_1),
        ))
        .id();
    let beneath = app
        .world_mut()
        .spawn((
            RigidBody::Dynamic,
            Collider::ball(0.25),
            GravityScale(0.),
            Velocity {
                linear: Vec3::Y * 2.,
                angular: Vec3::ZERO,
            },
            Transform::from_translation(roof - Vec3::Y * 0.75),
            CollisionGroups::new(Group::GROUP_2, Group::GROUP_1),
        ))
        .id();
    for _ in 0..150 {
        app.update();
    }
    assert!(
        app.world()
            .get::<Transform>(owner)
            .unwrap()
            .translation
            .length()
            < 0.0001,
        "own fixed child must neither repel nor receive reaction impulses through the carrier"
    );
    let at = app.world().get::<Transform>(visitor).unwrap().translation;
    assert!(
        at.y > roof.y + 0.1 && at.y < roof.y + 0.4,
        "actual body stands on source roof: {at} vs {roof}"
    );
    let below = app.world().get::<Transform>(beneath).unwrap().translation;
    assert!(
        below.y < roof.y - 0.1 && below.y > roof.y - 1.,
        "real body also meets the source roof back face: {below} vs {roof}"
    );
    // Disable only source owner exclusion: the identical overlapping pair must
    // now push the carrier, proving this isn't an empty noninteracting fixture.
    app.world_mut().entity_mut(hull).remove::<HullMesh>();
    app.world_mut()
        .entity_mut(owner)
        .insert(Sleeping::disabled());
    for _ in 0..40 {
        app.update();
    }
    assert!(
        app.world()
            .get::<Transform>(owner)
            .unwrap()
            .translation
            .length()
            > 0.01,
        "missing own-hull exception must reproduce self-contact"
    );
}
