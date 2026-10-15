use super::*;
#[test]
fn source_stance_clips_and_replicated_outfit_are_distinct() {
    assert_eq!(locomotion(0., "stand"), "Idle_Stand");
    assert_eq!(locomotion(4.49, "stand"), "Move_Walk");
    assert_eq!(locomotion(4.5, "stand"), "Move_Run");
    assert_eq!(locomotion(1., "prone"), "Move_Prone");
    assert_eq!(locomotion(0., "swim"), "Idle_Swim");
    let state = serde_json::json!({"inventory":{"worn":{"hat":27,"shirt":24,"pants":23},"wornQuality":{"shirt":51,"pants":0},"wornHasCondition":{"shirt":true,"pants":false}},"player":{"profile":{"face":14}}});
    let mut catalog = crate::entities::ItemCatalog::default();
    catalog.items.insert(
        24,
        serde_json::from_value(
            serde_json::json!({"id":24,"name":"Gold shirt","type":"shirt","hasCondition":true}),
        )
        .unwrap(),
    );
    let body = outfit(&state, false, &catalog);
    let arms = outfit(&state, true, &catalog);
    assert_eq!(body.gear, vec![27]);
    assert_eq!(body.face, Some(14));
    assert_eq!(body.shirt, 24);
    assert_eq!(body.wear, Vec2::new(0.48, 0.));
    assert_eq!(arms.shirt, 24);
    assert!(arms.gear.is_empty());
    assert!(arms.face.is_none());
}
#[test]
fn source_camera_pivot_orbit_and_crosshair_convergence() {
    assert!((camera::pivot(1.75, 2.) - 1.605).abs() < 1e-6);
    let mut view = Perspective {
        first_person: false,
        ..default()
    };
    let pose = camera::follow(
        &mut view,
        None,
        Entity::PLACEHOLDER,
        Vec3::ZERO,
        1.75,
        2.,
        0.,
        -45.,
        0.,
        1.,
    );
    assert!((pose.translation - Vec3::Y * 1.605).length() - 2. < 1e-5);
    assert!(
        pose.translation.y > 2.5,
        "offset rotates with look pitch, so body stays in frame"
    );
    let eye = Vec3::Y * 1.75;
    let fwd = pose.rotation * Vec3::NEG_Z;
    let target = pose.translation + fwd * 12.;
    let aim = camera::converge(eye, fwd, target, Vec3::NEG_Z);
    assert!(aim.dot((target - eye).normalize()) > 0.99999);
    assert_eq!(
        camera::converge(eye, Vec3::NEG_Z, eye + Vec3::Z, Vec3::NEG_Z),
        Vec3::NEG_Z
    );
}

#[test]
fn actual_rapier_shoulder_sweep_catches_thin_wall_and_ignores_dropped_items() {
    use bevy_rapier3d::prelude::*;
    #[derive(Resource, Default)]
    struct Results(Vec<f32>);
    fn probe(context: ReadRapierContext, mut result: ResMut<Results>) {
        let Ok(context) = context.single() else {
            return;
        };
        result.0 = vec![
            camera::sweep(&context, Vec3::Y * 1.605, Vec3::Z, 2., Entity::PLACEHOLDER),
            camera::sweep(&context, Vec3::Y * 1.605, Vec3::X, 2., Entity::PLACEHOLDER),
        ];
    }
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .init_resource::<Results>()
    .add_systems(Update, probe);
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(1., 2., 0.025),
        Transform::from_xyz(0., 1., 1.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::ball(0.1),
        Transform::from_xyz(1., 1.605, 0.),
        CollisionGroups::new(Group::GROUP_3, Group::ALL),
    ));
    for _ in 0..4 {
        app.update();
    }
    let result = &app.world().resource::<Results>().0;
    assert!(
        (0.56..0.59).contains(&result[0]),
        "sphere stops before actual 5cm wall: {result:?}"
    );
    assert_eq!(result[1], 2., "dropped can does not push source camera");
}

pub(super) fn avatar_app() -> App {
    use crate::{
        character::{CharacterPlugin, ClothesMaterial},
        content::ContentCache,
        rig::{RigPlugin, RigSkin},
    };
    let mut app = App::new();
    app.add_plugins((MinimalPlugins,AssetPlugin::default(),TransformPlugin,RigPlugin))
        .init_asset::<Mesh>().init_asset::<Image>().init_asset::<StandardMaterial>().init_asset::<bevy::shader::Shader>()
        .init_asset::<SkinnedMeshInverseBindposes>().init_resource::<ContentCache>().init_resource::<WorldConfig>()
        .init_resource::<WorldSessionEpoch>().init_resource::<PlayerInput>().init_resource::<crate::viewmodel::ViewmodelStatus>().init_resource::<Settings>()
        .init_resource::<crate::entities::ItemCatalog>().init_resource::<crate::vehicle::Occupancy>()
        .init_resource::<crate::world::interactables::PropOccupancy>()
        .init_resource::<ButtonInput<KeyCode>>().init_resource::<ButtonInput<MouseButton>>().init_resource::<AccumulatedMouseMotion>()
        .insert_resource(UiState{screen:crate::ui::Screen::Playing,..default()})
        .insert_resource({let mut m=UiModel::default();m.connected=true;m.alive=true;m})
        .insert_resource(GameSnapshot(serde_json::json!({"connected":true,"player":{"alive":true,"profile":{"face":14}},"inventory":{"worn":{"shirt":24,"pants":23,"hat":27},"wornQuality":{"shirt":100}}})))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .add_plugins((CharacterPlugin,AvatarPlugin));
    app.world_mut()
        .resource_mut::<crate::entities::ItemCatalog>()
        .items
        .insert(
            24,
            serde_json::from_value(
                serde_json::json!({"id":24,"name":"Gold shirt","type":"shirt","hasCondition":true}),
            )
            .unwrap(),
        );
    crate::player::origin_test_player(app.world_mut(), Vec3::new(4., 5., 6.), 0);
    for _ in 0..3 {
        app.update();
    }
    app
}
#[test]
fn live_player_clothes_follow_acceptance_arms_rebuild_and_camera_layer_without_rebuilding_for_wear()
{
    use crate::{character::ClothesMaterial, rig::RigSkin};
    let mut app = avatar_app();
    let (root, skin) = app
        .world_mut()
        .query_filtered::<(Entity, &RigSkin), With<LocalAvatar>>()
        .single(app.world())
        .map(|(e, s)| (e, s.0))
        .unwrap();
    let material = app
        .world()
        .get::<MeshMaterial3d<ClothesMaterial>>(skin)
        .unwrap()
        .0
        .clone();
    assert_eq!(
        app.world().get::<RenderLayers>(skin).unwrap(),
        &RenderLayers::layer(15)
    );
    assert_eq!(
        app.world().get::<RigPlayback>(root).unwrap().clip,
        "Move_Walk"
    );
    assert!(
        (app.world().get::<Transform>(root).unwrap().translation - Vec3::new(4., 5., 6.42))
            .length()
            < 1e-5
    );
    let arm = app.world_mut().spawn(FirstPersonArms).id();
    app.world_mut().resource_mut::<GameSnapshot>().0["inventory"]["wornQuality"]["shirt"] =
        serde_json::json!(20);
    app.update();
    assert_eq!(app.world().get::<Appearance>(arm).unwrap().shirt, 24);
    assert_eq!(app.world().get::<Appearance>(arm).unwrap().face, None);
    assert_eq!(
        app.world()
            .get::<MeshMaterial3d<ClothesMaterial>>(skin)
            .unwrap()
            .0,
        material,
        "condition changes uniforms without replacing material"
    );
    assert_eq!(
        app.world()
            .resource::<Assets<ClothesMaterial>>()
            .get(&material)
            .unwrap()
            .extension
            .settings
            .x,
        0.8
    );
    app.world_mut().resource_mut::<Perspective>().first_person = false;
    app.update();
    assert_eq!(
        app.world().get::<RenderLayers>(skin).unwrap(),
        &RenderLayers::layer(0)
    );
    assert_eq!(
        app.world().get::<Transform>(root).unwrap().translation,
        Vec3::new(4., 5., 6.)
    );
    app.world_mut().entity_mut(arm).despawn();
    let replacement = app.world_mut().spawn(FirstPersonArms).id();
    app.update();
    assert_eq!(
        app.world().get::<Appearance>(replacement).unwrap().shirt,
        24,
        "fresh held-item arms restore worn sleeves"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["inventory"]["worn"]["shirt"] =
        serde_json::json!(0);
    app.update();
    assert_eq!(
        app.world().get::<Appearance>(replacement).unwrap().shirt,
        -1,
        "accepted unwear clears sleeves"
    );
}

#[test]
fn real_body_weapon_uses_upper_clip_preserves_walking_legs_and_mirrors_accepted_attachment_changes()
{
    use crate::viewmodel::HeldMesh;
    use bevy::ecs::system::RunSystemOnce;
    let mut app = avatar_app();
    let arms = app
        .world_mut()
        .run_system_once(
            |mut commands: Commands,
             mut library: ResMut<RigLibrary>,
             mut meshes: ResMut<Assets<Mesh>>,
             mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
             mut materials: ResMut<Assets<StandardMaterial>>,
             config: Res<WorldConfig>| {
                let root = spawn_rig(
                    &mut commands,
                    &mut library,
                    &mut meshes,
                    &mut binds,
                    &config.content_root.join("rig.json"),
                    materials.add(StandardMaterial::default()),
                    Transform::from_xyz(0., -1.75, 0.12),
                    true,
                    "Eaglefire_Equip",
                )
                .unwrap();
                commands.entity(root).insert(FirstPersonArms);
                root
            },
        )
        .unwrap();
    let hook = app.world().get::<RigPlayback>(arms).unwrap().bones["Right_Hook"];
    {
        let mut rig = app.world_mut().get_mut::<RigPlayback>(arms).unwrap();
        rig.looping = false;
        rig.time = rig.data.anims["Eaglefire_Equip"].length;
    }
    let mesh = app
        .world_mut()
        .resource_mut::<Assets<Mesh>>()
        .add(Cuboid::from_size(Vec3::splat(0.1)));
    let material = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial::default());
    let gun = app
        .world_mut()
        .spawn((
            HeldMesh,
            Mesh3d(mesh.clone()),
            MeshMaterial3d(material.clone()),
            Transform::from_rotation(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)),
            Visibility::Inherited,
            ChildOf(hook),
        ))
        .id();
    let magazine = app
        .world_mut()
        .spawn((
            Mesh3d(mesh),
            MeshMaterial3d(material),
            Transform::from_xyz(0.1, 0.2, 0.3),
            Visibility::Inherited,
            ChildOf(gun),
        ))
        .id();
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["heldItemId"] = serde_json::json!(4);
    app.world_mut()
        .resource_mut::<crate::entities::ItemCatalog>()
        .items
        .insert(
            4,
            serde_json::from_value(
                serde_json::json!({"id":4,"name":"Eaglefire","type":"gun","asset":"eaglefire"}),
            )
            .unwrap(),
        );
    for _ in 0..3 {
        app.update();
    }
    let root = app.world().resource::<AvatarState>().root.unwrap();
    let body = app.world().get::<RigPlayback>(root).unwrap();
    assert_eq!(
        body.clip, "Move_Walk",
        "gun does not replace lower-body locomotion"
    );
    let leg = app
        .world()
        .get::<Transform>(body.bones["Left_Leg"])
        .unwrap();
    let legbone = body
        .data
        .bones
        .iter()
        .find(|b| b.name == "Left_Leg")
        .unwrap();
    let base = Transform {
        translation: Vec3::from_array(legbone.pos),
        rotation: Quat::from_array(legbone.rot),
        scale: Vec3::from_array(legbone.scale),
    };
    let walk = &body.data.anims["Move_Walk"];
    let expected = crate::rig::sample_track(
        &walk.tracks["Left_Leg"],
        body.time.rem_euclid(walk.length),
        base,
    );
    assert!(leg.rotation.angle_between(expected.rotation) < 1e-4);
    let mirror = app.world().resource::<held::Mirror>();
    let copy = mirror.copies[&gun];
    let mag_copy = mirror.copies[&magazine];
    assert_eq!(
        app.world().get::<ChildOf>(copy).unwrap().parent(),
        body.bones["Right_Hook"]
    );
    assert_eq!(app.world().get::<ChildOf>(mag_copy).unwrap().parent(), copy);
    assert_eq!(
        app.world().get::<Transform>(mag_copy).unwrap().translation,
        Vec3::new(0.1, 0.2, 0.3)
    );
    assert!(
        app.world()
            .get::<AvatarMuzzle>(root)
            .unwrap()
            .0
            .distance(Vec3::new(4., 5., 6.))
            < 3.
    );
    assert!(
        app.world()
            .get::<AvatarMuzzle>(root)
            .unwrap()
            .1
            .abs_diff_eq(
                app.world().get::<GlobalTransform>(copy).unwrap().rotation() * Vec3::Y,
                1e-5
            )
    );
    app.world_mut().entity_mut(magazine).despawn();
    app.update();
    assert!(
        app.world().get_entity(mag_copy).is_err(),
        "removed accepted attachment disappears from body too"
    );
    let vehicle = app.world_mut().spawn(Transform::IDENTITY).id();
    app.world_mut()
        .resource_mut::<crate::vehicle::Occupancy>()
        .seat = Some(crate::vehicle::OccupiedSeat {
        vehicle,
        net_id: 8,
        seat: 0,
        body_pose: Transform::IDENTITY,
        eye_pose: Transform::IDENTITY,
        velocity: Vec3::ZERO,
        driver: true,
        aircraft: false,
    });
    app.update();
    assert!(
        app.world().get_entity(copy).is_err(),
        "driver has both hands on wheel, not a rifle"
    );
}

#[test]
fn accepted_player_death_builds_original_physical_body_and_respawn_removes_it() {
    let mut app = avatar_app();
    app.world_mut()
        .spawn((crate::player::PlayerCamera, Transform::IDENTITY));
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["alive"] = serde_json::json!(false);
    for _ in 0..3 {
        app.update();
    }
    let corpse = app.world().resource::<death::Death>().corpse_for_test();
    assert!(corpse.is_some());
    let corpse = corpse.unwrap();
    assert_eq!(
        app.world().get::<Appearance>(corpse).unwrap().face,
        Some(14)
    );
    assert!(
        app.world()
            .get::<Appearance>(corpse)
            .unwrap()
            .gear
            .is_empty(),
        "original Die builds bare corpse, not a duplicated inventory"
    );
    assert!(
        app.world().get::<RigPlayback>(corpse).unwrap().paused,
        "physical bones own death pose"
    );
    assert_eq!(
        app.world_mut()
            .query::<&bevy_rapier3d::prelude::RigidBody>()
            .iter(app.world())
            .filter(|b| **b == bevy_rapier3d::prelude::RigidBody::Dynamic)
            .count(),
        11
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["alive"] = serde_json::json!(true);
    app.update();
    app.update();
    assert!(app.world().get_entity(corpse).is_err());
    assert_eq!(
        app.world_mut()
            .query::<&bevy_rapier3d::prelude::RigidBody>()
            .iter(app.world())
            .filter(|b| **b == bevy_rapier3d::prelude::RigidBody::Dynamic)
            .count(),
        0
    );
}

#[test]
fn aircraft_alt_input_uses_source_wrap_pitch_and_entry_reset_without_resetting_scroll_zoom() {
    let mut app = avatar_app();
    let vehicle = app.world_mut().spawn_empty().id();
    {
        let mut view = app.world_mut().resource_mut::<Perspective>();
        view.first_person = false;
        view.drive = Vec2::new(80., 40.);
        view.fly = Vec2::new(50., 30.);
        view.drive_zoom = 0.6;
    }
    app.world_mut()
        .resource_mut::<crate::vehicle::Occupancy>()
        .seat = Some(crate::vehicle::OccupiedSeat {
        vehicle,
        net_id: 40,
        seat: 0,
        driver: true,
        aircraft: true,
        body_pose: Transform::IDENTITY,
        eye_pose: Transform::IDENTITY,
        velocity: Vec3::ZERO,
    });
    app.update();
    {
        let view = app.world().resource::<Perspective>();
        assert_eq!(view.drive, Vec2::new(0., 15.));
        assert_eq!(view.fly, Vec2::ZERO);
        assert_eq!(
            view.drive_zoom, 0.6,
            "source entry preserves scroll distance"
        );
    }
    app.world_mut().resource_mut::<Settings>().mouse_sensitivity = 1.;
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::AltLeft);
    app.world_mut()
        .resource_mut::<AccumulatedMouseMotion>()
        .delta = Vec2::new(190., 90.);
    app.update();
    assert_eq!(
        app.world().resource::<Perspective>().fly,
        Vec2::new(170., 70.)
    );
    app.world_mut()
        .resource_mut::<AccumulatedMouseMotion>()
        .delta = Vec2::ZERO;
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::AltLeft);
    app.update();
    let view = app.world().resource::<Perspective>();
    assert!(
        view.fly.x > 0. && view.fly.x < 170. && view.fly.y > 0. && view.fly.y < 70.,
        "release smoothly returns aircraft look"
    );
}

#[test]
fn accepted_lean_rotates_real_spine_without_tilting_root_or_walking_legs_and_clears_in_chair() {
    let mut app = avatar_app();
    app.world_mut().resource_mut::<Time<Virtual>>().pause();
    app.insert_resource(crate::player::lean::LeanState::default());
    app.update();
    let root = app.world().resource::<AvatarState>().root.unwrap();
    let rig = app.world().get::<RigPlayback>(root).unwrap();
    let spine = rig.bones["Spine"];
    let leg = rig.bones["Left_Leg"];
    let root_pose = *app.world().get::<Transform>(root).unwrap();
    let before = app.world().get::<Transform>(spine).unwrap().rotation;
    let leg_pose = *app.world().get::<Transform>(leg).unwrap();
    app.world_mut()
        .resource_mut::<crate::player::lean::LeanState>()
        .angle = 20_f32.to_radians();
    app.update();
    let after = app.world().get::<Transform>(spine).unwrap().rotation;
    assert!((before.angle_between(after).to_degrees() - 20.).abs() < 0.01);
    assert_eq!(*app.world().get::<Transform>(root).unwrap(), root_pose);
    assert_eq!(*app.world().get::<Transform>(leg).unwrap(), leg_pose);
    app.world_mut()
        .resource_mut::<crate::world::interactables::PropOccupancy>()
        .seat = Some(crate::world::interactables::OccupiedPropSeat {
        net_id: 44,
        body_pose: Transform::IDENTITY,
        eye_pose: Transform::IDENTITY,
        anchor: Vec3::ZERO,
        exit_position: Vec3::X,
    });
    app.update();
    assert_eq!(
        app.world().get::<pose::TorsoPose>(root).unwrap().lean,
        0.,
        "sitting never carries the old walking lean into the chair pose"
    );
}
