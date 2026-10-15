use super::*;
use crate::impact::surfaces::{SurfaceOverride, SurfaceTable};
use bevy::{
    audio::{Decodable, Volume},
    ecs::system::SystemState,
};

fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin {
            file_path: WorldConfig::default()
                .content_root
                .canonicalize()
                .unwrap()
                .to_string_lossy()
                .into_owned(),
            ..default()
        },
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ))
    .init_asset::<AudioSource>()
    .init_resource::<WorldConfig>()
    .init_resource::<WorldState>()
    .init_resource::<crate::world::WorldGeometry>()
    .init_resource::<SurfaceTable>()
    .init_resource::<WorldOrigin>()
    .init_resource::<RainState>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Occupancy>()
    .init_resource::<PlayerInput>()
    .init_resource::<crate::settings::Settings>()
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .init_resource::<UiModel>()
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ));
    {
        let mut model = app.world_mut().resource_mut::<UiModel>();
        model.connected = true;
        model.alive = true;
    }
    app.world_mut().spawn((
        crate::player::PlayerCamera,
        Transform::from_xyz(0., 1.6, 0.),
    ));
    app
}
fn floor(app: &mut App, surface: Surface) -> Entity {
    app.world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(20., 0.1, 20.),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            SurfaceOverride(surface),
            Transform::from_xyz(0., -0.1, 0.),
        ))
        .id()
}
fn probe(app: &mut App, at: Vec3) -> Option<Surface> {
    let mut system = SystemState::<Ground>::new(app.world_mut());
    system
        .get(app.world())
        .unwrap()
        .surface(at, Entity::PLACEHOLDER)
}
fn tick(app: &mut App, count: usize) {
    for _ in 0..count {
        app.update();
    }
}
fn messages(app: &mut App) -> Vec<Footfall> {
    app.world_mut()
        .resource_mut::<Messages<Footfall>>()
        .drain()
        .collect()
}

#[test]
fn original_material_gait_fallback_and_clips_decode() {
    let root = WorldConfig::default().content_root;
    let mut banks = Banks::load(&root);
    let mut previous = String::new();
    for seed in 0..20 {
        let path = banks.step(Surface::Dirt, true, seed).unwrap();
        assert!(
            path.contains("dirt_walk"),
            "missing dirt_run must retain dirt"
        );
        assert_ne!(path, previous);
        previous = path;
    }
    for surface in [
        Surface::Concrete,
        Surface::Grass,
        Surface::Dirt,
        Surface::Metal,
        Surface::Wood,
        Surface::Sand,
        Surface::Water,
        Surface::Gravel,
        Surface::Snow,
        Surface::Ice,
        Surface::Rock,
    ] {
        for run in [false, true] {
            let path = banks.step(surface, run, 32).unwrap();
            let source = AudioSource {
                bytes: std::fs::read(root.join(&path)).unwrap().into(),
            };
            assert!(
                source.decoder().any(|sample| sample.abs() > 0.001),
                "empty source: {path}"
            );
        }
        let path = banks.pick("landing", land_surface(surface), 51).unwrap();
        if surface == Surface::Metal {
            assert!(path.contains("landing/metal_"));
        }
        let source = AudioSource {
            bytes: std::fs::read(root.join(&path)).unwrap().into(),
        };
        assert!(source.decoder().any(|sample| sample.abs() > 0.001));
    }
    for speed in [1., 3., 4.] {
        assert!(banks.pick("swim", swim_bank(speed), 2).is_some());
    }
}

#[test]
fn real_ground_probe_distinguishes_air_material_water_and_only_world_layer() {
    let mut app = app();
    let ground = floor(&mut app, Surface::Metal);
    tick(&mut app, 3);
    assert_eq!(probe(&mut app, Vec3::ZERO), Some(Surface::Metal));
    assert_eq!(probe(&mut app, Vec3::Y), None);
    app.world_mut()
        .entity_mut(ground)
        .insert(CollisionGroups::new(Group::GROUP_2, Group::ALL));
    tick(&mut app, 2);
    assert_eq!(
        probe(&mut app, Vec3::ZERO),
        None,
        "players never stand in for world floor"
    );
    app.world_mut().resource_mut::<WorldState>().sea_level = Some(0.);
    assert_eq!(
        probe(&mut app, Vec3::new(100., 0.09, 100.)),
        Some(Surface::Water)
    );
    assert_eq!(probe(&mut app, Vec3::new(100., 0.11, 100.)), None);
}

#[test]
fn puddle_audio_requires_field_hard_ground_and_actual_roof_clearance() {
    let mut app = app();
    let ground = floor(&mut app, Surface::Concrete);
    app.world_mut().resource_mut::<RainState>().puddle = 1.;
    tick(&mut app, 3);
    let wet = (-80..80)
        .flat_map(|x| (-80..80).map(move |z| Vec2::new(x as f32 / 4., z as f32 / 4.)))
        .find(|p| puddle_wet(*p, 1.))
        .unwrap();
    let at = Vec3::new(wet.x, 0., wet.y);
    assert_eq!(probe(&mut app, at), Some(Surface::Water));
    assert_eq!(
        probe(&mut app, Vec3::ZERO),
        Some(Surface::Concrete),
        "wet weather alone does not make a puddle"
    );
    let roof = app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(2., 0.01, 2.),
            Transform::from_translation(at + Vec3::Y * 3.),
        ))
        .id();
    tick(&mut app, 2);
    assert_eq!(
        probe(&mut app, at),
        Some(Surface::Concrete),
        "thin ceiling blocks puddle sound"
    );
    app.world_mut()
        .entity_mut(roof)
        .insert(Collider::cuboid(0.25, 0.1, 0.25));
    tick(&mut app, 2);
    assert_eq!(
        probe(&mut app, at),
        Some(Surface::Water),
        "tiny overhead prop is not shelter"
    );
    app.world_mut()
        .entity_mut(ground)
        .insert(SurfaceOverride(Surface::Grass));
    assert_eq!(probe(&mut app, at), Some(Surface::Grass));
    app.world_mut()
        .entity_mut(ground)
        .insert(SurfaceOverride(Surface::Concrete));
    app.world_mut().resource_mut::<RainState>().puddle = 0.34;
    assert_eq!(probe(&mut app, at), Some(Surface::Concrete));
    // The shader and CPU consume the same rebased coordinate, including negatives.
    let offset = Vec2::new(256., 512.);
    assert_eq!(puddle_wet(wet, 1.), puddle_wet((wet - offset) + offset, 1.));
}

#[test]
fn local_resolved_motion_steps_and_precontact_velocity_lands_once() {
    let mut app = app();
    floor(&mut app, Surface::Metal);
    app.init_resource::<LocalSteps>()
        .add_message::<Footfall>()
        .add_systems(
            FixedUpdate,
            (local_steps, local_swim)
                .chain()
                .before(PhysicsSet::SyncBackend),
        );
    app.world_mut()
        .insert_resource(PlayerInput::with_movement(Vec2::Y));
    let body = app
        .world_mut()
        .spawn((
            Transform::default(),
            PlayerBody::for_motion_test(Vec3::X * 5., true, "sprint"),
        ))
        .id();
    let mut sounds = Vec::new();
    for _ in 0..22 {
        app.update();
        sounds.extend(messages(&mut app));
    }
    assert_eq!(sounds.len(), 1);
    assert!(matches!(sounds[0].sound, Sound::Step(Surface::Metal, true)));
    assert_eq!(sounds[0].db, -4.);
    app.world_mut()
        .entity_mut(body)
        .insert(PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"));
    tick(&mut app, 2);
    assert!(messages(&mut app).is_empty());
    app.world_mut().entity_mut(body).insert((
        PlayerBody::for_motion_test(Vec3::new(0., -10., 0.), false, "stand"),
        KinematicCharacterControllerOutput {
            grounded: true,
            ..default()
        },
    ));
    use bevy::ecs::system::RunSystemOnce;
    app.world_mut().run_system_once(local_landing).unwrap();
    let sounds = messages(&mut app);
    assert_eq!(sounds.len(), 1);
    assert!(matches!(sounds[0].sound, Sound::Landing(Surface::Metal)));
    assert_eq!(sounds[0].db, 0.);
    app.world_mut()
        .entity_mut(body)
        .insert(PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"));
    app.world_mut().run_system_once(local_landing).unwrap();
    assert!(messages(&mut app).is_empty());
    app.world_mut()
        .entity_mut(body)
        .insert(PlayerBody::for_motion_test(Vec3::X * 4., false, "swim"));
    let mut sounds = Vec::new();
    for _ in 0..20 {
        app.update();
        sounds.extend(messages(&mut app));
    }
    assert!(
        sounds
            .iter()
            .any(|s| matches!(s.sound, Sound::Swim("heavywading")))
    );
}

#[test]
fn voices_reach_playback_settings_pause_range_and_epoch_cleanup_without_device() {
    let mut app = app();
    app.add_plugins(FootstepPlugin);
    app.update();
    let event = Footfall::step(Vec3::ZERO, Surface::Grass, "crouch", 2., 0);
    app.world_mut().write_message(event);
    app.update();
    let mut query = app
        .world_mut()
        .query::<(Entity, &PlaybackSettings, &AudioPlayer)>();
    let (entity, settings, _) = query.single(app.world()).unwrap();
    assert!((0.94..=1.06).contains(&settings.speed));
    assert_eq!(
        settings.volume,
        Volume::Linear(
            voices::gain(-12., 4., 30., 1.6)
                * app
                    .world()
                    .resource::<crate::settings::Settings>()
                    .master_volume
        )
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    app.update();
    assert!(app.world().get::<PlaybackSettings>(entity).unwrap().paused);
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(entity).is_err());
    assert_eq!(voices::gain(-7., 4., 30., 30.), 0.);
    let near = voices::gain(-7., 4., 30., 4.);
    let far = voices::gain(-7., 4., 30., 8.);
    assert!((near / far - (2. * 26. / 22.)).abs() < 0.0001);
}

#[test]
fn shelter_includes_world_dynamic_roofs_and_excludes_other_collision_layers() {
    let mut app = app();
    let roof = app
        .world_mut()
        .spawn((
            RigidBody::Dynamic,
            GravityScale(0.),
            Collider::cuboid(2., 0.01, 2.),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            Transform::from_xyz(0., 3., 0.),
        ))
        .id();
    tick(&mut app, 3);
    let mut query = SystemState::<Shelter>::new(app.world_mut());
    assert!(query.get(app.world()).unwrap().covered(Vec3::ZERO));
    app.world_mut().entity_mut(roof).insert((
        RigidBody::Fixed,
        CollisionGroups::new(Group::GROUP_2, Group::ALL),
    ));
    tick(&mut app, 2);
    assert!(!query.get(app.world()).unwrap().covered(Vec3::ZERO));
}

#[test]
fn remote_probe_silences_airborne_seated_dead_and_snapped_puppets_then_lands() {
    use crate::avatar::remotes::FootMotion;
    let mut app = app();
    floor(&mut app, Surface::Grass);
    app.init_resource::<RemoteSteps>()
        .add_message::<Footfall>()
        .add_systems(Update, remote_steps);
    let peer = app
        .world_mut()
        .spawn((
            Transform::default(),
            RemotePlayer::for_footstep_test(FootMotion {
                speed: 5.,
                stance: "sprint",
                ..default()
            }),
        ))
        .id();
    let mut sounds = Vec::new();
    for _ in 0..22 {
        app.update();
        sounds.extend(messages(&mut app));
    }
    assert_eq!(sounds.len(), 1);
    assert!(matches!(sounds[0].sound, Sound::Step(Surface::Grass, true)));
    app.world_mut()
        .get_mut::<Transform>(peer)
        .unwrap()
        .translation
        .y = 3.;
    tick(&mut app, 25);
    assert!(messages(&mut app).is_empty());
    app.world_mut()
        .get_mut::<Transform>(peer)
        .unwrap()
        .translation
        .y = 0.;
    {
        let mut p = app.world_mut().get_mut::<RemotePlayer>(peer).unwrap();
        p.foot_motion.speed = 0.;
        p.foot_motion.vertical_speed = -8.;
    }
    app.update();
    let sounds = messages(&mut app);
    assert_eq!(sounds.len(), 1);
    assert!(matches!(sounds[0].sound, Sound::Landing(Surface::Grass)));
    for reason in 0..3 {
        {
            let mut p = app.world_mut().get_mut::<RemotePlayer>(peer).unwrap();
            p.foot_motion.speed = 20.;
            p.foot_motion.seated = reason == 0;
            p.alive = reason != 1;
            p.foot_motion.snap = reason == 2;
        }
        tick(&mut app, 8);
        assert!(messages(&mut app).is_empty());
    }
    app.world_mut().despawn(peer);
    app.update();
    assert!(app.world().resource::<RemoteSteps>().players.is_empty());
}
