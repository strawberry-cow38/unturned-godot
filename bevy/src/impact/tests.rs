use super::*;
use serde_json::json;
fn app() -> App {
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
    .init_asset::<ForwardDecalMaterial<StandardMaterial>>()
    .init_asset::<AudioSource>()
    .init_asset::<bevy::shader::Shader>()
    .add_plugins(bevy::pbr::decal::ForwardDecalPlugin)
    .init_resource::<WorldConfig>()
    .init_resource::<crate::world::WorldGeometry>()
    .init_resource::<crate::world::WorldState>()
    .init_resource::<crate::entities::ItemCatalog>()
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(
        json!({"connected":true,"playerId":1,"player":{"alive":true}}),
    ))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_message::<CoreEvent>()
    .add_plugins((crate::particles::ParticlePlugin, ImpactPlugin));
    app.world_mut().spawn((
        PlayerCamera,
        Camera::default(),
        Transform::from_xyz(0., 1.6, 3.),
    ));
    app.update();
    app.update();
    app
}
fn event(app: &mut App, sequence: u64, kind: &str, data: serde_json::Value) {
    app.world_mut().write_message(CoreEvent {
        sequence,
        kind: kind.into(),
        data,
    });
}
fn emitters(app: &mut App) -> usize {
    app.world_mut()
        .query::<&particles::Emitter>()
        .iter(app.world())
        .count()
}
fn markers(app: &mut App) -> usize {
    app.world_mut()
        .query::<&markers::Marker>()
        .iter(app.world())
        .count()
}
#[test]
fn accepted_animal_bullet_and_wire_impact_emit_one_blood_burst_and_owner_marker() {
    let mut app = app();
    let hit = json!({"netId":17,"damage":40,"point":[0,1,0],"direction":[0,0,-1],"attacker":1,"source":"bullet","impactEmitted":true});
    event(&mut app, 1, "animalHit", hit.clone());
    event(
        &mut app,
        2,
        "impact",
        json!({"pos":{"x":0,"y":1,"z":0},"surface":0}),
    );
    app.update();
    assert_eq!(
        emitters(&mut app),
        1,
        "bullet fact and wire echo must not double-render flesh"
    );
    assert_eq!(markers(&mut app), 1);
    let emitter = app
        .world_mut()
        .query::<&particles::Emitter>()
        .single(app.world())
        .unwrap();
    assert_eq!(emitter.debug_state().1, 4);
    event(&mut app, 1, "animalHit", hit);
    event(&mut app, 2, "impact", json!({"pos":[0,1,0],"surface":0}));
    app.update();
    assert_eq!(emitters(&mut app), 1);
    assert_eq!(
        markers(&mut app),
        1,
        "retained event replay must be ignored"
    );
    event(
        &mut app,
        3,
        "animalHit",
        json!({"netId":17,"point":[0,1,0],"direction":[0,0,-1],"attacker":1,"source":"melee","impactEmitted":false}),
    );
    app.update();
    assert_eq!(emitters(&mut app), 2);
    assert_eq!(
        markers(&mut app),
        2,
        "melee has no wire impact and must render its own accepted burst"
    );
    for _ in 0..18 {
        app.update();
    }
    assert_eq!(
        markers(&mut app),
        0,
        "source .33-second markers must expire independently of one-second blood"
    );
    assert_eq!(emitters(&mut app), 2);
    for _ in 0..40 {
        app.update();
    }
    assert_eq!(emitters(&mut app), 0);
}
#[test]
fn remote_hit_blood_does_not_claim_local_marker_and_epoch_drops_old_fx() {
    let mut app = app();
    event(
        &mut app,
        1,
        "animalHit",
        json!({"point":[0,1,0],"direction":[0,0,-1],"attacker":2,"source":"melee","impactEmitted":false}),
    );
    app.update();
    assert_eq!(emitters(&mut app), 1);
    assert_eq!(markers(&mut app), 0);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert_eq!(emitters(&mut app), 0);
    event(
        &mut app,
        2,
        "animalHit",
        json!({"point":[0,1,0],"attacker":1,"source":"blast","impactEmitted":false}),
    );
    app.update();
    assert_eq!(
        emitters(&mut app),
        0,
        "source blast has no standalone flesh impact"
    );
    assert_eq!(markers(&mut app), 0, "source blast has no aimed hitmarker");
}
#[test]
fn actual_prop_minority_boxes_and_map_layers_preserve_source_materials() {
    let config = WorldConfig::default();
    let table = surfaces::SurfaceTable::load(&config);
    assert_eq!(table.prop("ac_0", Vec3::ZERO, false), Surface::Metal);
    assert_eq!(
        table.prop("ac_0_detached_trim", Vec3::ZERO, false),
        Surface::Metal
    );
    assert_eq!(
        table.prop("dock_1", Vec3::new(0., 18., -20.), false),
        Surface::Concrete
    );
    assert_eq!(
        table.prop("dock_1", Vec3::new(18., 18., -20.), false),
        Surface::Metal
    );
    assert_eq!(
        table.prop("dock_1_child", Vec3::new(18., 18., -20.), false),
        Surface::Concrete,
        "minority boxes apply only to the exact authored prefab"
    );
    assert_eq!(
        table.prop("New_Rail_Custom", Vec3::ZERO, false),
        Surface::Rails
    );
    assert_eq!(Surface::terrain("Farm Wheat"), Surface::Grass);
    assert_eq!(Surface::terrain("Stone 01"), Surface::Rock);
    assert_eq!(Surface::terrain("Snow"), Surface::Snow);
    assert_eq!(Surface::Rock.bullet_bank(), "rock");
    assert_eq!(Surface::Rails.bullet_bank(), "concrete");
}
#[test]
fn source_blood_sheet_and_snow_debris_have_real_assets_and_correct_motion() {
    let mut app = app();
    let root = app.world().resource::<WorldConfig>().content_root.clone();
    let mut system = bevy::ecs::system::SystemState::<(
        ResMut<Feedback>,
        Res<WorldConfig>,
        Res<AssetServer>,
        ResMut<Assets<StandardMaterial>>,
    )>::new(app.world_mut());
    let (mut feedback, config, assets, mut materials) = system.get_mut(app.world_mut()).unwrap();
    let (material, sheet) = feedback.material(
        "blood",
        "impact_flesh_dynamic_0.png",
        Color::WHITE,
        0.46,
        &config,
        &assets,
        &mut materials,
    );
    assert!(sheet);
    let blood = blood_spec(material.clone(), Vec3::NEG_Z, sheet);
    assert_eq!(blood.direction, Vec3::Z);
    assert_eq!(blood.atlas, UVec2::new(4, 1));
    assert_eq!(blood.animation_speed, Vec2::ZERO);
    assert!(!blood.animation_loop);
    let snow = debris_spec(Surface::Snow, material, Vec3::Y, true);
    assert_eq!(snow.speed, Vec2::new(1.6, 3.4));
    assert_eq!(snow.gravity.y, -4.2);
    assert!(snow.animation_loop);
    assert_eq!(snow.animation_speed, Vec2::ZERO);
    let mut bank = crate::sound::Bank::load(&root, "bulletimpacts", "metallight");
    assert!(bank.clips.len() >= 8);
    let mut previous = String::new();
    for seed in 0..32 {
        let next = bank.pick(seed).unwrap().to_owned();
        assert_ne!(previous, next);
        previous = next;
    }
}

#[test]
fn accepted_world_impact_recovers_actual_collider_normal_and_projects_source_hole() {
    let mut app = app();
    app.world_mut().spawn((
        crate::world::WorldObject {
            guid: String::new(),
            asset_name: "ac_0".into(),
            source_index: 0,
        },
        Collider::cuboid(1., 1., 0.1),
        RigidBody::Fixed,
        Transform::from_xyz(0., 1., 0.),
    ));
    for _ in 0..4 {
        app.update();
    }
    event(
        &mut app,
        1,
        "impact",
        json!({"pos":{"x":0,"y":1,"z":0.1},"surface":1}),
    );
    app.update();
    assert_eq!(emitters(&mut app), 1);
    let (transform, material) = app
        .world_mut()
        .query_filtered::<(
            &Transform,
            &MeshMaterial3d<ForwardDecalMaterial<StandardMaterial>>,
        ), With<Hole>>()
        .single(app.world())
        .unwrap();
    assert!((transform.translation.z - 0.16).abs() < 0.002);
    assert!((transform.rotation * Vec3::Y).distance(Vec3::Z) < 0.001);
    assert_eq!(transform.scale, Vec3::new(0.4, 0.3, 0.4));
    let material = app
        .world()
        .resource::<Assets<ForwardDecalMaterial<StandardMaterial>>>()
        .get(&material.0)
        .unwrap();
    assert!(material.base.base_color_texture.is_some());
    for _ in 0..910 {
        app.update();
    }
    assert_eq!(
        app.world_mut().query::<&Hole>().iter(app.world()).count(),
        0
    );
}
#[test]
fn accepted_hitmarker_uses_overlay_ui_camera_above_the_weapon_pass() {
    let mut app = app();
    app.world_mut().spawn((
        Camera {
            order: 1,
            ..default()
        },
        Transform::IDENTITY,
    ));
    let overlay = app
        .world_mut()
        .spawn((
            Camera {
                order: 2,
                ..default()
            },
            IsDefaultUiCamera,
            Transform::IDENTITY,
        ))
        .id();
    event(&mut app, 1, "hit", json!({"headshot":false}));
    app.update();
    let marker = app
        .world_mut()
        .query_filtered::<Entity, With<markers::Marker>>()
        .single(app.world())
        .unwrap();
    assert!(
        app.world().get::<UiTargetCamera>(marker).is_none(),
        "targeting the order0 world camera lets order1 ADS cover confirmed hitmarkers"
    );
    let mut system =
        bevy::ecs::system::SystemState::<bevy::ui::DefaultUiCamera>::new(app.world_mut());
    assert_eq!(system.get(app.world()).unwrap().get(), Some(overlay));
}
#[test]
fn moving_decal_parent_uses_current_physics_pose_before_render_propagation() {
    let mut app = app();
    let root = app.world_mut().spawn(Transform::from_xyz(5., 0., 0.)).id();
    let child = app
        .world_mut()
        .spawn((Transform::from_xyz(0., 1., 2.), ChildOf(root)))
        .id();
    app.update();
    app.world_mut()
        .get_mut::<Transform>(root)
        .unwrap()
        .translation
        .x = 8.;
    assert_eq!(
        app.world()
            .get::<GlobalTransform>(child)
            .unwrap()
            .translation()
            .x,
        5.
    );
    let mut system = bevy::ecs::system::SystemState::<surfaces::CurrentPoses>::new(app.world_mut());
    let surfaces = system.get(app.world()).unwrap();
    let pose = surfaces.current_pose(child).unwrap();
    assert_eq!(pose.translation(), Vec3::new(8., 1., 2.));
    let hole = Transform::from_xyz(8., 1., 2.2);
    let local = GlobalTransform::from(hole).reparented_to(&pose);
    assert!(
        pose.transform_point(local.translation)
            .distance(hole.translation)
            < 1e-5
    );
}

#[test]
fn accepted_water_contact_ignores_solid_probe_and_retained_pierce_does_not_duplicate_splash() {
    let mut app = app();
    app.world_mut().spawn((
        crate::world::WorldObject {
            guid: String::new(),
            asset_name: "ac_0".into(),
            source_index: 0,
        },
        Collider::cuboid(1., 1., 0.1),
        RigidBody::Fixed,
        Transform::from_xyz(0., 1., 0.),
    ));
    for _ in 0..4 {
        app.update();
    }
    let impact = json!({"pos":[0,1,0.1],"surface":1,"normal":[0,1,0],"water":true,"thin":false});
    event(&mut app, 1, "impact", impact.clone());
    event(
        &mut app,
        2,
        "bulletPierced",
        json!({"point":[0,1,0.1],"water":true}),
    );
    app.update();
    let (name, pose, emitter) = app
        .world_mut()
        .query::<(&Name, &Transform, &particles::Emitter)>()
        .single(app.world())
        .unwrap();
    assert_eq!(name.as_str(), "Accepted Water impact");
    assert_eq!(pose.translation, Vec3::new(0., 1., 0.1));
    assert_eq!(emitter.debug_state().1, 2);
    assert_eq!(
        app.world_mut().query::<&Hole>().iter(app.world()).count(),
        0
    );
    event(&mut app, 1, "impact", impact);
    app.update();
    assert_eq!(
        emitters(&mut app),
        1,
        "the retained contact and continuation diagnostic are one splash"
    );
}

#[test]
fn accepted_world_normal_and_position_survive_a_different_later_probe_contact() {
    let mut app = app();
    app.world_mut().spawn((
        crate::world::WorldObject {
            guid: String::new(),
            asset_name: "ac_0".into(),
            source_index: 0,
        },
        Collider::cuboid(1., 1., 0.1),
        RigidBody::Fixed,
        Transform::from_xyz(0., 1., 0.),
    ));
    for _ in 0..4 {
        app.update();
    }
    let point = Vec3::new(0., 1., 0.2);
    let normal = Vec3::new(0., 1., 1.).normalize();
    event(
        &mut app,
        1,
        "impact",
        json!({"pos":point.to_array(),"surface":1,"normal":normal.to_array(),"water":false}),
    );
    app.update();
    let pose = app
        .world_mut()
        .query_filtered::<&Transform, With<Hole>>()
        .single(app.world())
        .unwrap();
    assert!(pose.translation.distance(point + normal * 0.06) < 1e-5);
    assert!((pose.rotation * Vec3::Y).distance(normal) < 1e-5);
}

#[test]
fn accepted_ocean_warhead_has_one_source_large_column_at_sea_level() {
    let mut app = app();
    app.world_mut()
        .resource_mut::<crate::world::WorldState>()
        .sea_level = Some(7.);
    let blast = json!({"position":[4,9,6],"radius":15,"itemId":519});
    event(&mut app, 1, "bulletExplosion", blast.clone());
    app.update();
    let (name, pose, emitter) = app
        .world_mut()
        .query::<(&Name, &Transform, &particles::Emitter)>()
        .single(app.world())
        .unwrap();
    assert_eq!(name.as_str(), "Accepted ocean blast splash");
    assert_eq!(pose.translation, Vec3::new(4., 7., 6.));
    assert_eq!(emitter.debug_state().1, 24);
    event(&mut app, 1, "bulletExplosion", blast);
    event(
        &mut app,
        2,
        "bulletExplosion",
        json!({"position":[4,9.01,6],"radius":15}),
    );
    app.update();
    assert_eq!(
        emitters(&mut app),
        1,
        "source upper limit is sea + 2, with retained facts deduplicated"
    );
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert_eq!(emitters(&mut app), 0);
}
