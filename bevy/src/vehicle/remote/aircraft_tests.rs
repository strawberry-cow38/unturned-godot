use super::*;
mod visual;
use crate::vehicle::render::aircraft::{AircraftPart, PartKind};
use bevy::ecs::system::RunSystemOnce;

fn app(key: &'static str) -> (App, Entity) {
    let mut app = super::super::tests::physics_app(None);
    let config = WorldConfig::default();
    app.add_plugins(AssetPlugin {
        file_path: config.content_root.to_string_lossy().into_owned(),
        ..default()
    })
    .register_required_components::<Mesh3d, Visibility>()
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<AudioSource>()
    .init_resource::<ContentCache>()
    .init_resource::<Settings>()
    .init_resource::<audio::AudioVoices>()
    .insert_resource(config)
    .insert_resource(GameSnapshot(
        json!({"network":{"ownsWorld":false},"playerId":2}),
    ))
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        networked: true,
        ..default()
    })
    .add_systems(
        PostUpdate,
        (render::aircraft::animate, audio::update)
            .chain()
            .after(step_bodies),
    );
    let body = super::super::tests::body(&mut app, key, Vec3::Y * 100.);
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    {
        let mut b = app.world_mut().get_mut::<VehicleBody>(body).unwrap();
        let spec = &data::named(key).unwrap().spec;
        b.flight = flight::Flight::new(
            spec,
            flight::ignition_duration(spec, &WorldConfig::default().content_root),
        );
        b.flags = 0;
    }
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>,
                  config: Res<WorldConfig>| {
                render::build(
                    &mut commands,
                    body,
                    data::named(key).unwrap(),
                    0,
                    &config.content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    (app, body)
}
fn ticks(app: &mut App, count: usize) {
    for _ in 0..count {
        app.update();
    }
}
fn loop_sound(
    app: &mut App,
    body: Entity,
) -> Option<(Entity, Handle<AudioSource>, PlaybackSettings)> {
    app.world_mut()
        .query::<(Entity, &ChildOf, &AudioPlayer, &PlaybackSettings)>()
        .iter(app.world())
        .find(|(_, p, _, s)| {
            p.parent() == body && matches!(s.mode, bevy::audio::PlaybackMode::Loop)
        })
        .map(|(e, _, p, s)| (e, p.0.clone(), *s))
}

#[test]
fn remote_rotors_and_original_audio_advance_without_any_physical_airframe_step() {
    let (mut app, body) = app("huey");
    ticks(&mut app, 2);
    assert!(loop_sound(&mut app, body).is_none());
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags = 1;
    ticks(&mut app, 100);
    let b = app.world().get::<VehicleBody>(body).unwrap();
    let spool = b.flight.spool;
    let phases = [b.flight.main_phase, b.flight.tail_phase];
    assert!(spool > 0.15 && spool < 0.6);
    assert_eq!(b.flight.ignition_starts, 1);
    assert_eq!(b.flight.collective, 0.);
    assert_eq!(b.flight.lift_acceleration, Vec3::ZERO);
    assert_eq!(b.fuel, data::named("huey").unwrap().spec.fuel);
    assert_eq!(
        app.world().get::<Transform>(body).unwrap().translation,
        Vec3::Y * 100.
    );
    assert_eq!(
        *app.world().get::<RigidBody>(body).unwrap(),
        RigidBody::KinematicPositionBased
    );
    assert_eq!(
        app.world().get::<ExternalForce>(body).unwrap().force,
        Vec3::ZERO
    );
    assert_eq!(
        app.world().get::<Velocity>(body).unwrap().linear,
        Vec3::ZERO
    );
    let mut rotor_count = 0;
    for (part, pose) in app
        .world_mut()
        .query::<(&AircraftPart, &Transform)>()
        .iter(app.world())
    {
        let expected = match part.kind {
            PartKind::MainRotor => Quat::from_rotation_y(phases[0]),
            PartKind::TailRotor => {
                Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)
                    * Quat::from_rotation_y(phases[1])
            }
            _ => continue,
        };
        assert!(pose.rotation.abs_diff_eq(expected, 0.00001));
        rotor_count += 1;
    }
    assert_eq!(rotor_count, 2);
    let (_, handle, playback) =
        loop_sound(&mut app, body).expect("remote engine loop is actually materialized");
    let spec = &data::named("huey").unwrap().spec;
    assert_eq!(
        handle.path().unwrap().path().to_string_lossy(),
        spec.sound.as_deref().unwrap()
    );
    assert!(
        (playback.speed - spec.idle_pitch.lerp(spec.max_pitch, spool) * flight::size_pitch(spec))
            .abs()
            < 0.00001
    );
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags = 0;
    ticks(&mut app, 1);
    assert!(
        loop_sound(&mut app, body).is_some(),
        "source loop follows spinning blades after switch off"
    );
    ticks(&mut app, 300);
    assert_eq!(
        app.world().get::<VehicleBody>(body).unwrap().flight.spool,
        0.
    );
    assert!(loop_sound(&mut app, body).is_none());
}

#[test]
fn fixed_clock_handoff_pause_and_explosion_never_double_step_or_run_remote_forces() {
    let mut app = super::super::tests::physics_app(None);
    app.insert_resource(GameSnapshot(
        json!({"network":{"ownsWorld":false},"playerId":2}),
    ))
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        networked: true,
        ..default()
    });
    let body = super::super::tests::body(&mut app, "huey", Vec3::Y * 100.);
    let spec = &data::named("huey").unwrap().spec;
    for index in 0..90 {
        if index == 30 {
            app.world_mut()
                .get_mut::<VehicleBody>(body)
                .unwrap()
                .driver_player_id = 2;
        }
        if index == 60 {
            app.world_mut()
                .get_mut::<VehicleBody>(body)
                .unwrap()
                .driver_player_id = 1;
        }
        let mut expected = app.world().get::<VehicleBody>(body).unwrap().flight.clone();
        expected.advance_presentation(spec, true, false, 0.02);
        app.update();
        let b = app.world().get::<VehicleBody>(body).unwrap();
        assert_eq!(b.flight.spool, expected.spool, "handoff tick{index}");
        assert_eq!(b.flight.main_phase, expected.main_phase);
        assert_eq!(b.flight.ignition_starts, 1);
    }
    let phase = app
        .world()
        .get::<VehicleBody>(body)
        .unwrap()
        .flight
        .main_phase;
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    ticks(&mut app, 5);
    assert_ne!(
        app.world()
            .get::<VehicleBody>(body)
            .unwrap()
            .flight
            .main_phase,
        phase,
        "remote network pause menu does not pause the world"
    );
    app.world_mut().resource_mut::<UiState>().networked = false;
    let spool = app.world().get::<VehicleBody>(body).unwrap().flight.spool;
    ticks(&mut app, 5);
    assert_eq!(
        app.world().get::<VehicleBody>(body).unwrap().flight.spool,
        spool,
        "the existing main pause gate still precedes all clocks"
    );
    app.world_mut().resource_mut::<UiState>().networked = true;
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags = 32;
    let phase = app
        .world()
        .get::<VehicleBody>(body)
        .unwrap()
        .flight
        .main_phase;
    ticks(&mut app, 5);
    assert_eq!(
        app.world()
            .get::<VehicleBody>(body)
            .unwrap()
            .flight
            .main_phase,
        phase
    );
}

#[test]
fn remote_plane_propeller_uses_original_disc_threshold_and_explosion_retires_loop() {
    let (mut app, body) = app("otter");
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags = 1;
    ticks(&mut app, 100);
    let b = app.world().get::<VehicleBody>(body).unwrap();
    assert!((b.flight.spool - 0.625).abs() < 0.00001);
    let phase = b.flight.prop_phase;
    let mut checked = 0;
    for (part, pose, visibility) in app
        .world_mut()
        .query::<(&AircraftPart, &Transform, &Visibility)>()
        .iter(app.world())
    {
        match part.kind {
            PartKind::Propeller => {
                assert!(
                    pose.rotation
                        .abs_diff_eq(Quat::from_rotation_z(phase), 0.00001)
                );
                checked += 1;
            }
            PartKind::PropBlades => {
                assert_eq!(*visibility, Visibility::Hidden);
                checked += 1;
            }
            PartKind::PropDisc => {
                assert_eq!(*visibility, Visibility::Inherited);
                checked += 1;
            }
            _ => (),
        }
    }
    assert_eq!(checked, 3, "actual source propeller hierarchy");
    let (sound, _, _) = loop_sound(&mut app, body).unwrap();
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags = 32;
    ticks(&mut app, 2);
    assert_eq!(
        app.world()
            .get::<VehicleBody>(body)
            .unwrap()
            .flight
            .prop_phase,
        phase
    );
    assert!(app.world().get_entity(sound).is_err());
}
