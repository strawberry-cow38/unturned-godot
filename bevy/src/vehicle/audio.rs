//! Original engine/ignition/horn clips, source RPM curves, and starter torque gate.
use super::{VehicleBody, data::VehicleSpec, flight};
use crate::{settings::Settings, world::WorldConfig};
use bevy::{
    audio::{AudioSinkPlayback, SpatialScale, Volume},
    prelude::*,
};
use std::collections::{HashMap, HashSet};

const ENGINE_GAIN: f32 = 2.8;
const WIND_DOWN_SECONDS: f32 = 1.4;

#[derive(Clone, Debug)]
pub struct Engine {
    pub starts: u64,
    pub horns: u64,
    pub cranking: f32,
    pub exhaust_puff: f32,
    was_running: bool,
    ignition_length: f32,
    horn_cooldown: f32,
}
impl Engine {
    pub fn new(ignition_length: f32, running: bool) -> Self {
        Self {
            starts: 0,
            horns: 0,
            cranking: 0.,
            exhaust_puff: 0.,
            was_running: running,
            ignition_length,
            horn_cooldown: 0.,
        }
    }
    pub fn step(&mut self, spec: &VehicleSpec, running: bool, dt: f32) {
        self.horn_cooldown = (self.horn_cooldown - dt).max(0.);
        if running && !self.was_running {
            self.starts += 1;
            self.exhaust_puff = 1.;
            if !spec.heli {
                self.cranking = if self.ignition_length > 0.3 {
                    self.ignition_length.min(2.5)
                } else {
                    1.2
                };
            }
        }
        self.was_running = running;
        self.cranking = (self.cranking - dt).max(0.);
        if running {
            self.exhaust_puff = (self.exhaust_puff - dt).max(0.);
        }
    }
    pub fn horn(&mut self, spec: &VehicleSpec) {
        if spec.horn.is_some() && self.horn_cooldown <= 0. {
            self.horns += 1;
            self.horn_cooldown = if spec.horn_cooldown > 0. {
                spec.horn_cooldown
            } else {
                0.5
            };
        }
    }
}
pub fn ignition_path(spec: &VehicleSpec) -> Option<&str> {
    spec.ignition_sound
        .as_deref()
        .or_else(|| (!spec.heli && !spec.plane).then_some("car_ignition.wav"))
}

#[derive(Default)]
struct Voice {
    entity: Option<Entity>,
    starts: u64,
    horns: u64,
    pitch: f32,
    gain: f32,
    wind: Option<(f32, f32, f32)>,
}
impl Voice {
    fn target(
        &mut self,
        spec: &VehicleSpec,
        running: bool,
        cranking: f32,
        rpm: f32,
        spool: f32,
        exploded: bool,
        dt: f32,
    ) -> bool {
        if exploded {
            self.gain = 0.;
            self.wind = None;
            return false;
        }
        let aircraft = spec.heli || spec.plane;
        if aircraft {
            self.wind = None;
            self.pitch = spec.idle_pitch.lerp(spec.max_pitch, spool) * flight::size_pitch(spec);
            self.gain = (spec.idle_volume * 0.35).lerp(spec.max_volume, spool) * ENGINE_GAIN;
            return spool > 0.01;
        }
        if running && cranking <= 0. {
            let n = ((rpm - 1000.) / 5000.).clamp(0., 1.);
            self.pitch = spec.idle_pitch.lerp(spec.max_pitch, n);
            self.gain = spec.idle_volume.lerp(spec.max_volume, n) * ENGINE_GAIN;
            self.wind = None;
            return true;
        }
        if self.entity.is_none() {
            self.gain = 0.;
            return false;
        }
        let (mut elapsed, pitch, gain) = self.wind.unwrap_or((0., self.pitch, self.gain));
        elapsed += dt;
        let k = (elapsed / WIND_DOWN_SECONDS).clamp(0., 1.);
        let ease = 1. - (1. - k) * (1. - k);
        self.pitch = pitch.lerp(spec.idle_pitch * 0.45, ease);
        self.gain = gain * (1. - ease);
        self.wind = Some((elapsed, pitch, gain));
        k < 1.
    }
}
#[derive(Resource, Default)]
pub struct AudioVoices(HashMap<Entity, Voice>);
#[derive(Component)]
pub struct VehicleSound {
    pub gain: f32,
    pub max_distance: f32,
}

pub fn update(
    mut commands: Commands,
    mut voices: ResMut<AudioVoices>,
    bodies: Query<(Entity, &VehicleBody)>,
    mut sounds: Query<(
        &mut VehicleSound,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
    time: Res<Time>,
    ui: Option<Res<crate::ui::UiState>>,
) {
    let live: HashSet<_> = bodies.iter().map(|(entity, _)| entity).collect();
    voices.0.retain(|entity, voice| {
        if live.contains(entity) {
            true
        } else {
            if let Some(sound) = voice.entity {
                if let Ok(mut e) = commands.get_entity(sound) {
                    e.despawn();
                }
            }
            false
        }
    });
    // Retire disappeared vehicles even while paused, but retain active decoder
    // position and the original wind-down envelope until SP simulation resumes.
    if ui.as_deref().is_some_and(crate::player::simulation_paused) {
        return;
    }
    for (entity, body) in &bodies {
        let spec = &super::data::definition(body.type_id).unwrap().spec;
        let voice = voices.0.entry(entity).or_default();
        // Aircraft latch ignition in StepHeli/StepPlane until the disc has fully
        // stopped. A switch off/on during spool-down must not replay the starter.
        let starts = if spec.heli || spec.plane {
            body.flight.ignition_starts
        } else {
            body.engine.starts
        };
        if starts > voice.starts {
            if let Some(path) = ignition_path(spec) {
                if config.content_root.join(path).is_file() {
                    spawn_sound(
                        &mut commands,
                        &assets,
                        entity,
                        path,
                        false,
                        ENGINE_GAIN * if spec.heli { 2. } else { 1. },
                        flight::size_pitch(spec),
                        if spec.heli { 34. } else { 10. },
                        if spec.heli { 520. } else { 80. },
                        settings.master_volume,
                    );
                }
            }
        }
        voice.starts = starts;
        if body.engine.horns > voice.horns {
            if let Some(path) = spec.horn.as_deref() {
                if config.content_root.join(path).is_file() {
                    let range = if spec.horn_range > 0. {
                        spec.horn_range
                    } else {
                        90.
                    };
                    spawn_sound(
                        &mut commands,
                        &assets,
                        entity,
                        path,
                        false,
                        10_f32.powf(4. / 20.),
                        1.,
                        range / 7.5,
                        range,
                        settings.master_volume,
                    );
                }
            }
        }
        voice.horns = body.engine.horns;
        let playing = voice.target(
            spec,
            body.flags & 1 != 0,
            body.engine.cranking,
            body.drivetrain.rpm,
            body.flight.spool,
            body.flags & 32 != 0,
            time.delta_secs(),
        );
        if !playing {
            if let Some(sound) = voice.entity.take() {
                if let Ok(mut e) = commands.get_entity(sound) {
                    e.despawn();
                }
            }
            continue;
        }
        if let Some(sound) = voice.entity {
            if let Ok((mut gain, mut playback, sink)) = sounds.get_mut(sound) {
                gain.gain = voice.gain;
                playback.speed = voice.pitch;
                if let Some(sink) = sink {
                    sink.set_speed(voice.pitch);
                }
            }
        } else if let Some(path) = spec.sound.as_deref() {
            if config.content_root.join(path).is_file() {
                voice.entity = Some(spawn_sound(
                    &mut commands,
                    &assets,
                    entity,
                    path,
                    true,
                    voice.gain,
                    voice.pitch,
                    if spec.heli { 52. } else { 26. },
                    if spec.heli { 800. } else { 300. },
                    settings.master_volume,
                ));
            }
        }
    }
}
pub(crate) fn spawn_sound(
    commands: &mut Commands,
    assets: &AssetServer,
    parent: impl Into<Option<Entity>>,
    path: &str,
    looping: bool,
    gain: f32,
    pitch: f32,
    unit: f32,
    distance: f32,
    master: f32,
) -> Entity {
    let entity = commands
        .spawn((
            AudioPlayer::new(assets.load(path.to_owned())),
            (if looping {
                PlaybackSettings::LOOP
            } else {
                PlaybackSettings::DESPAWN
            })
            .with_spatial(true)
            .with_spatial_scale(SpatialScale::new(1. / unit))
            .with_volume(Volume::Linear(gain * master))
            .with_speed(pitch.max(0.01)),
            VehicleSound {
                gain,
                max_distance: distance,
            },
            Transform::default(),
        ))
        .id();
    if let Some(parent) = parent.into() {
        commands.entity(entity).insert(ChildOf(parent));
    }
    entity
}
pub fn volume(
    settings: Res<Settings>,
    ui: Option<Res<crate::ui::UiState>>,
    listeners: Query<&GlobalTransform, With<SpatialListener>>,
    mut sounds: Query<(
        &VehicleSound,
        &GlobalTransform,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = ui.as_deref().is_some_and(crate::player::simulation_paused);
    let listener = listeners.iter().next().map(GlobalTransform::translation);
    for (gain, pose, mut playback, sink) in &mut sounds {
        let in_range = listener
            .is_none_or(|listener| listener.distance(pose.translation()) < gain.max_distance);
        let volume = Volume::Linear(if in_range {
            gain.gain * settings.master_volume
        } else {
            0.
        });
        playback.volume = volume;
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
            if paused {
                sink.pause();
            } else {
                sink.play();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actual_voice_settings_pause_pending_decoders_but_keep_network_menus_live() {
        use crate::ui::{Screen, UiState};
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .init_resource::<Settings>()
            .insert_resource(UiState {
                screen: Screen::Paused,
                ..default()
            })
            .add_systems(Update, volume);
        let source = app
            .world_mut()
            .spawn((
                VehicleSound {
                    gain: 0.8,
                    max_distance: 80.,
                },
                GlobalTransform::IDENTITY,
                PlaybackSettings::LOOP,
            ))
            .id();
        app.update();
        assert!(
            app.world().get::<PlaybackSettings>(source).unwrap().paused,
            "a newly loaded sink must begin paused, not leak the siren while paused"
        );
        app.world_mut().resource_mut::<UiState>().networked = true;
        app.update();
        assert!(!app.world().get::<PlaybackSettings>(source).unwrap().paused);
        app.world_mut().resource_mut::<UiState>().networked = false;
        app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
        app.update();
        assert!(!app.world().get::<PlaybackSettings>(source).unwrap().paused);
    }
    #[test]
    fn actual_vehicle_update_keeps_wind_down_alive_while_singleplayer_is_paused() {
        use crate::ui::{Screen, UiState};
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<AudioSource>()
            .init_resource::<Settings>()
            .init_resource::<WorldConfig>()
            .init_resource::<AudioVoices>()
            .insert_resource(UiState {
                screen: Screen::Paused,
                ..default()
            })
            .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
                std::time::Duration::from_millis(200),
            ))
            .add_systems(Update, update);
        let car = super::super::tests::body(&mut app, "jeep", Vec3::ZERO);
        app.world_mut().get_mut::<VehicleBody>(car).unwrap().flags = 0;
        let sound = app
            .world_mut()
            .spawn((
                VehicleSound {
                    gain: 1.,
                    max_distance: 300.,
                },
                PlaybackSettings::LOOP,
            ))
            .id();
        let spec = &super::super::data::named("jeep").unwrap().spec;
        let mut voice = Voice {
            entity: Some(sound),
            ..default()
        };
        assert!(voice.target(spec, true, 0., 6000., 0., false, 0.02));
        assert!(voice.target(spec, false, 0., 1000., 0., false, 0.25));
        let before = (voice.wind, voice.pitch, voice.gain);
        app.world_mut()
            .resource_mut::<AudioVoices>()
            .0
            .insert(car, voice);
        for _ in 0..12 {
            app.update();
        }
        let voice = &app.world().resource::<AudioVoices>().0[&car];
        assert_eq!((voice.wind, voice.pitch, voice.gain), before);
        assert!(
            app.world().get_entity(sound).is_ok(),
            "paused envelope must not retire the voice"
        );
        app.world_mut().resource_mut::<UiState>().networked = true;
        for _ in 0..12 {
            app.update();
        }
        assert!(
            app.world().get_entity(sound).is_err(),
            "network pause menus keep world audio clocks alive"
        );
        assert!(
            app.world().resource::<AudioVoices>().0[&car]
                .entity
                .is_none()
        );
    }
    #[test]
    fn original_car_clip_withholds_engine_torque_and_loop_until_crank_finishes() {
        let spec = &super::super::data::named("jeep").unwrap().spec;
        let seconds = flight::ignition_duration(
            spec,
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content"),
        );
        assert!(
            (1.8..2.2).contains(&seconds),
            "original car ignition is 2.04 seconds, got {seconds}"
        );
        let mut engine = Engine::new(seconds, false);
        let mut voice = Voice::default();
        for _ in 0..50 {
            engine.step(spec, true, 0.02);
            assert!(!voice.target(spec, true, engine.cranking, 1000., 0., false, 0.02));
        }
        assert_eq!(engine.starts, 1);
        for _ in 0..60 {
            engine.step(spec, true, 0.02);
        }
        assert_eq!(engine.cranking, 0.);
        assert!(voice.target(spec, true, engine.cranking, 1000., 0., false, 0.02));
        assert!((voice.gain - spec.idle_volume * 2.8).abs() < 1e-5);
    }
    #[test]
    fn stopped_car_sags_in_pitch_then_stops_decoding_after_source_wind_down() {
        let spec = &super::super::data::named("jeep").unwrap().spec;
        let mut voice = Voice {
            entity: Some(Entity::PLACEHOLDER),
            ..default()
        };
        assert!(voice.target(spec, true, 0., 6000., 0., false, 0.02));
        let initial = (voice.pitch, voice.gain);
        assert!(voice.target(spec, false, 0., 1000., 0., false, 0.7));
        assert!(voice.pitch < initial.0 && voice.gain < initial.1 * 0.3);
        assert!(!voice.target(spec, false, 0., 1000., 0., false, 0.71));
        assert_eq!(voice.gain, 0.);
    }
}
