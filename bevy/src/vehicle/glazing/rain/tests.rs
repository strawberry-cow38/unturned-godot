use super::*;
use crate::{
    content::ContentCache,
    player::GameSnapshot,
    vehicle::{
        damage::WreckMaterial,
        glazing::{self, Glazing},
    },
    world::WorldConfig,
};
use bevy::ecs::system::RunSystemOnce;
use bevy_rapier3d::prelude::*;

#[test]
fn dominant_face_folds_opposites_votes_by_area_and_keeps_first_tie() {
    let mut points = vec![[0., 0., 0.], [4., 0., 0.], [0., 3., 0.]];
    points.extend([[0., 3., 0.], [4., 0., 0.], [0., 0., 0.]]);
    points.extend([[0., 0., 0.], [0., 4., 0.], [0., 0., 4.]]);
    assert_eq!(
        dominant_axis(&points),
        Vec3::Z,
        "front+back area12 beats side8"
    );
    assert_eq!(dominant_axis(&[[0., 0., 0.]; 3]), Vec3::ZERO);
    let tie = [
        [0., 0., 0.],
        [0., 2., 0.],
        [0., 0., 2.],
        [0., 0., 0.],
        [2., 0., 0.],
        [0., 2., 0.],
    ];
    assert_eq!(dominant_axis(&tie), Vec3::X);
}
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
    .init_asset::<GlassMaterial>()
    .init_resource::<ContentCache>()
    .init_resource::<RainState>()
    .init_resource::<WreckMaterial>()
    .insert_resource(GameSnapshot(serde_json::json!({})))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(Update, (glazing::adopt, cameras, poll).chain())
    .add_systems(PostUpdate, materials.after(TransformSystems::Propagate));
    app
}
fn vehicle(app: &mut App, key: &'static str, pose: Transform) -> Entity {
    let body = crate::vehicle::tests::body(app, key, pose.translation);
    app.world_mut()
        .entity_mut(body)
        .insert((pose, RigidBody::Fixed));
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>| {
                glazing::build(
                    &mut commands,
                    body,
                    crate::vehicle::data::named(key).unwrap(),
                    &WorldConfig::default().content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                );
            },
        )
        .unwrap();
    body
}
#[test]
fn actual_original_panes_keep_one_pass_damage_lifetime_and_source_wreck_override() {
    let mut app = app();
    let pose = Transform::from_xyz(800., 3., -650.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        0.7,
        0.2,
        -0.1,
    ));
    let body = vehicle(&mut app, "sedan", pose);
    let camera = app.world_mut().spawn(Camera3d::default()).id();
    app.update();
    let panes = app.world().get::<Glazing>(body).unwrap().panes.clone();
    assert_eq!(panes.len(), 6);
    assert_eq!(
        app.world()
            .get::<ScreenSpaceTransmission>(camera)
            .unwrap()
            .steps,
        1
    );
    let first = panes[0];
    let material = app
        .world()
        .get::<MeshMaterial3d<GlassMaterial>>(first)
        .unwrap()
        .0
        .clone();
    let uniform = &app
        .world()
        .resource::<Assets<GlassMaterial>>()
        .get(&material)
        .unwrap()
        .uniform;
    assert!(
        uniform.axis.xyz().y.abs() > 0.1 && uniform.axis.xyz().z.abs() > 0.5,
        "original raked windshield, {:?}",
        uniform.axis
    );
    let original_axis = uniform.axis;
    let before = uniform.world_from_local;
    assert!(
        app.world()
            .get::<MeshMaterial3d<StandardMaterial>>(first)
            .is_none()
    );
    assert_eq!(uniform.surface.x, 0.35);
    assert_eq!(uniform.surface.y, 0.1);
    let camera2 = app.world_mut().spawn(Camera3d::default()).id();
    app.world_mut()
        .entity_mut(body)
        .insert(Transform::from_xyz(-12., 8., 48.).with_rotation(Quat::from_rotation_z(0.5)));
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        serde_json::json!({"vehicles":[{"netId":1,"glassMask":63,"brokenGlassMask":1}]});
    app.update();
    let uniform = &app
        .world()
        .resource::<Assets<GlassMaterial>>()
        .get(&material)
        .unwrap()
        .uniform;
    assert_eq!(
        uniform.axis, original_axis,
        "mesh-local grid never rotates inside the pane"
    );
    assert_ne!(uniform.world_from_local, before);
    assert_eq!(
        *app.world().get::<Visibility>(first).unwrap(),
        Visibility::Hidden
    );
    assert_eq!(
        app.world()
            .get::<ScreenSpaceTransmission>(camera2)
            .unwrap()
            .steps,
        1
    );
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags |= 32;
    app.update();
    assert!(
        app.world()
            .get::<MeshMaterial3d<GlassMaterial>>(first)
            .is_none()
    );
    let black = &app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(first)
        .unwrap()
        .0;
    let black = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(black)
        .unwrap();
    assert!(black.base_color.to_srgba().red < 0.051);
    assert_eq!(black.alpha_mode, AlphaMode::Opaque);
    // Delayed native geometry on an already-charred hull must not reinstall
    // a live rain shader (the original recursive wreck override covers it).
    app.world_mut()
        .get_mut::<VehicleBody>(body)
        .unwrap()
        .wreck
        .charred = true;
    let late_base = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial::default());
    let late = app
        .world_mut()
        .spawn((
            RainPane {
                body,
                axis: Vec3::Z,
            },
            Pane {
                index: 0,
                center: Vec3::ZERO,
                size: Vec3::ONE,
                debug_color: None,
            },
            MeshMaterial3d(late_base),
            Transform::IDENTITY,
            ChildOf(body),
        ))
        .id();
    app.update();
    assert!(
        app.world()
            .get::<MeshMaterial3d<GlassMaterial>>(late)
            .is_none()
    );
    let handle = &app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(late)
        .unwrap()
        .0;
    assert!(
        app.world()
            .resource::<Assets<StandardMaterial>>()
            .get(handle)
            .unwrap()
            .base_color
            .to_srgba()
            .red
            < 0.051
    );
    app.world_mut().despawn(body);
    for pane in panes {
        assert!(
            app.world().get_entity(pane).is_err(),
            "materials do not keep ghost pane entities alive"
        );
    }
}
#[test]
fn source_above_hull_shelter_poll_ignores_own_roof_and_clears_when_rain_stops() {
    let mut app = app();
    let body = vehicle(&mut app, "sedan", Transform::from_xyz(0., 1., 0.));
    let top = top_local_y(crate::vehicle::data::named("sedan").unwrap());
    let own_roof = app
        .world_mut()
        .spawn((
            Collider::cuboid(2., 0.05, 2.),
            Transform::from_xyz(0., top + 0.95, 0.),
            RigidBody::Fixed,
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    app.world_mut().resource_mut::<RainState>().intensity = 1.;
    for _ in 0..30 {
        app.update();
    }
    assert!(
        !app.world().get::<Cover>(body).unwrap().covered,
        "probe starts above the source hull, not above each side pane"
    );
    let garage = app
        .world_mut()
        .spawn((
            Collider::cuboid(4., 0.05, 4.),
            Transform::from_xyz(0., top + 4., 0.),
            RigidBody::Fixed,
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    for _ in 0..30 {
        app.update();
    }
    assert!(app.world().get::<Cover>(body).unwrap().covered);
    app.world_mut().resource_mut::<RainState>().intensity = 0.;
    for _ in 0..30 {
        app.update();
    }
    assert!(
        !app.world().get::<Cover>(body).unwrap().covered,
        "source removes shelter gate between storms"
    );
    app.world_mut().despawn(garage);
    app.world_mut().despawn(own_roof);
}

#[test]
fn source_tree_pause_freezes_shelter_poll_but_shader_time_keeps_advancing() {
    let mut app = app();
    let body = vehicle(&mut app, "sedan", Transform::IDENTITY);
    for _ in 0..3 {
        app.update();
    }
    app.insert_resource(crate::ui::UiState {
        screen: crate::ui::Screen::Paused,
        ..default()
    });
    let remaining = app.world().get::<Cover>(body).unwrap().remaining;
    let pane = app.world().get::<Glazing>(body).unwrap().panes[0];
    let material = app
        .world()
        .get::<MeshMaterial3d<GlassMaterial>>(pane)
        .unwrap()
        .0
        .clone();
    let before = app
        .world()
        .resource::<Assets<GlassMaterial>>()
        .get(&material)
        .unwrap()
        .uniform
        .weather
        .w;
    for _ in 0..10 {
        app.update();
    }
    assert_eq!(app.world().get::<Cover>(body).unwrap().remaining, remaining);
    let after = app
        .world()
        .resource::<Assets<GlassMaterial>>()
        .get(&material)
        .unwrap()
        .uniform
        .weather
        .w;
    assert!(
        after > before + 0.19,
        "Godot shader TIME is unaffected by tree pause"
    );
}

#[path = "visual.rs"]
mod visual;
