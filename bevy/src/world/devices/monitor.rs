//! `HeartMonitor` on `Science_3`: an ECG shader quad 4 mm in front of the
//! modelled trace, ON by default, F toggles it, lit while on AND fed (the
//! mains; its own plug is unwireable over the server). Beating units beep as
//! the sweep crosses the R wave; flatlined ones hold the flat tone. Each unit is
//! alive with `AliveChance` 0.7, rolled per client as the source rolls it per
//! world build. Client-local, as in the source.
use super::*;
use bevy::{
    asset::Asset,
    audio::{SpatialScale, Volume},
    mesh::{Indices, MeshVertexBufferLayoutRef, PrimitiveTopology},
    pbr::{MaterialPipeline, MaterialPipelineKey},
    render::render_resource::{AsBindGroup, RenderPipelineDescriptor, ShaderType, SpecializedMeshPipelineError},
    shader::ShaderRef,
};

pub(crate) const PROP: &str = "Science_3";
/// The screen face, measured off Science_3.obj (mesh units, Z up).
pub(crate) const TRACE_Y: f32 = 0.2181;
pub(crate) const OVERLAY_GAP: f32 = 0.004;
pub(crate) const SCREEN_X0: f32 = -0.3171;
pub(crate) const SCREEN_X1: f32 = 0.3204;
pub(crate) const SCREEN_Z0: f32 = 1.2020;
pub(crate) const SCREEN_Z1: f32 = 1.7970;
/// `BeatPeriod` (60 bpm) and `FlatlinePeriod`.
pub(crate) const BEAT_PERIOD: f32 = 1.;
pub(crate) const FLATLINE_PERIOD: f32 = 2.2;
pub(crate) const ALIVE_CHANCE: f32 = 0.7;
/// `RPhase`: matches the shader's R position, so sound and picture agree.
pub(crate) const R_PHASE: f32 = 0.42;
/// `ScreenOffColor`: palette texel (1,0), (53,53,53).
const SCREEN_OFF: Color = Color::srgb(53. / 255., 53. / 255., 53. / 255.);
/// The AudioStreamPlayer3D: unit 7, max 26 m, -21 dB.
const AUDIO_UNIT: f32 = 7.;
const AUDIO_RANGE: f32 = 26.;
const AUDIO_DB: f32 = -21.;

#[derive(Clone, Copy, Debug, ShaderType)]
pub(crate) struct EcgParams {
    /// time_s, alive, lit, period.
    pub clock: Vec4,
    /// sag.
    pub extra: Vec4,
}
#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
pub(crate) struct EcgMaterial {
    #[uniform(0)]
    pub params: EcgParams,
}
impl Material for EcgMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/devices/ecg.wesl".into()
    }
    fn specialize(
        _: &MaterialPipeline,
        descriptor: &mut RenderPipelineDescriptor,
        _: &MeshVertexBufferLayoutRef,
        _: MaterialPipelineKey<Self>,
    ) -> Result<(), SpecializedMeshPipelineError> {
        // render_mode cull_disabled.
        descriptor.primitive.cull_mode = None;
        Ok(())
    }
}

/// `HeartMonitor.Build`: the overlay quad in the prop's frame, UV (1 - u, v).
pub(crate) fn screen_mesh() -> Mesh {
    let (w, h) = (SCREEN_X1 - SCREEN_X0, SCREEN_Z1 - SCREEN_Z0);
    let corner = |u: f32, v: f32| {
        (
            [SCREEN_X0 + u * w, TRACE_Y + OVERLAY_GAP, SCREEN_Z0 + v * h],
            [1. - u, v],
        )
    };
    let corners = [corner(0., 0.), corner(1., 0.), corner(1., 1.), corner(0., 1.)];
    let normal = (Vec3::from_array(corners[1].0) - Vec3::from_array(corners[0].0))
        .cross(Vec3::from_array(corners[2].0) - Vec3::from_array(corners[0].0))
        .normalize();
    Mesh::new(PrimitiveTopology::TriangleList, default())
        .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, corners.map(|c| c.0).to_vec())
        .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, corners.map(|c| c.1).to_vec())
        .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, vec![normal.to_array(); 4])
        .with_inserted_indices(Indices::U32(vec![0, 1, 2, 0, 2, 3]))
}

#[derive(Component, Debug)]
pub(crate) struct Monitor {
    pub(crate) index: usize,
    pub(crate) alive: bool,
    pub(crate) on: bool,
    pub(crate) screen: Entity,
    pub(crate) lit: Option<bool>,
    last_beat: f32,
    off: Handle<StandardMaterial>,
    ecg: Handle<EcgMaterial>,
}
impl Monitor {
    pub(crate) fn period(&self) -> f32 {
        if self.alive { BEAT_PERIOD } else { FLATLINE_PERIOD }
    }
}

/// `HeartMonitor.GlobalSeconds`: one clock for the whole ward.
#[derive(Resource, Default)]
pub(crate) struct Clock {
    pub(crate) seconds: f64,
    seed: u64,
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn attach(
    mut commands: Commands,
    state: Res<DeviceState>,
    mut clock: ResMut<Clock>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut standard: ResMut<Assets<StandardMaterial>>,
    mut ecg: Option<ResMut<Assets<EcgMaterial>>>,
    mut quad: Local<Option<Handle<Mesh>>>,
    mut off: Local<Option<Handle<StandardMaterial>>>,
    objects: Query<(Entity, &WorldObject), (Added<WorldObject>, With<PlacementEuler>)>,
) {
    for (entity, object) in &objects {
        if object.asset_name != PROP {
            continue;
        }
        let Some(ecg) = ecg.as_mut() else { continue };
        if clock.seed == 0 {
            clock.seed = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0x5EED, |d| d.as_nanos() as u64)
                | 1;
        }
        // GD.Randf() < AliveChance, per placement.
        let alive = crate::particles::random(&mut clock.seed) < ALIVE_CHANCE;
        let mesh = quad.get_or_insert_with(|| meshes.add(screen_mesh())).clone();
        let off = off
            .get_or_insert_with(|| {
                standard.add(StandardMaterial {
                    base_color: SCREEN_OFF,
                    perceptual_roughness: 1.,
                    ..default()
                })
            })
            .clone();
        let material = ecg.add(EcgMaterial {
            params: EcgParams {
                clock: Vec4::new(0., if alive { 1. } else { 0. }, 0., if alive { BEAT_PERIOD } else { FLATLINE_PERIOD }),
                extra: Vec4::new(1., 0., 0., 0.),
            },
        });
        let screen = commands
            .spawn((
                Name::new("ECG screen"),
                Mesh3d(mesh),
                MeshMaterial3d(off.clone()),
                bevy::light::NotShadowCaster,
                Transform::IDENTITY,
                ChildOf(entity),
            ))
            .id();
        commands.entity(entity).insert((
            Device(Target::Monitor(object.source_index)),
            Monitor {
                index: object.source_index,
                alive,
                on: !state.monitor_off.contains(&object.source_index),
                screen,
                lit: None,
                last_beat: -1.,
                off,
                ecg: material,
            },
        ));
    }
}

/// F: `Toggle`.
pub(crate) fn press(
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    focus: Res<InteractionFocus>,
    mut state: ResMut<DeviceState>,
    mut monitors: Query<(&Device, &mut Monitor)>,
) {
    for (device, mut monitor) in &mut monitors {
        if pressed_target(&ui, &settings, &keys, &mouse, &focus, device.0) {
            monitor.on = !monitor.on;
            if monitor.on {
                state.monitor_off.remove(&monitor.index);
            } else {
                state.monitor_off.insert(monitor.index);
            }
        }
    }
}

#[derive(Component)]
pub(crate) struct Flatline {
    monitor: Entity,
    epoch: u64,
}

/// `HubTick` + `Refresh`: swap the screen, drive the shared clock into the
/// shader, beep on the R wave or hold the flat tone.
#[allow(clippy::too_many_arguments)]
pub(crate) fn tick(
    mut commands: Commands,
    time: Res<Time>,
    mains: Res<Mains>,
    epoch: Res<WorldSessionEpoch>,
    config: Res<crate::world::WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
    settings: Option<Res<Settings>>,
    mut clock: ResMut<Clock>,
    mut materials: Option<ResMut<Assets<EcgMaterial>>>,
    listener: Query<&GlobalTransform, With<PlayerCamera>>,
    mut monitors: Query<(Entity, &mut Monitor, &GlobalTransform)>,
    screens: Query<Has<MeshMaterial3d<EcgMaterial>>>,
    mut flats: Query<(Entity, &Flatline, &mut PlaybackSettings, Option<&mut SpatialAudioSink>)>,
) {
    clock.seconds += f64::from(time.delta_secs());
    let master = settings.map_or(1., |s| s.master_volume);
    let ear = listener.single().ok().map(|t| t.translation());
    let gain = |at: Vec3| {
        ear.map_or(0., |ear| crate::sound::distance_gain(AUDIO_DB, AUDIO_UNIT, AUDIO_RANGE, at.distance(ear)) * master)
    };
    let can_play = audio.is_some();
    let mut flat_wanted: HashMap<Entity, Vec3> = HashMap::new();
    for (entity, mut monitor, pose) in &mut monitors {
        let lit = monitor.on && mains.live;
        if monitor.lit != Some(lit) {
            monitor.lit = Some(lit);
            if let Ok(has_ecg) = screens.get(monitor.screen) {
                if lit && !has_ecg {
                    commands
                        .entity(monitor.screen)
                        .remove::<MeshMaterial3d<StandardMaterial>>()
                        .insert(MeshMaterial3d(monitor.ecg.clone()));
                } else if !lit && has_ecg {
                    commands
                        .entity(monitor.screen)
                        .remove::<MeshMaterial3d<EcgMaterial>>()
                        .insert(MeshMaterial3d(monitor.off.clone()));
                }
            }
            if !lit {
                monitor.last_beat = -1.;
            }
        }
        if !lit {
            continue;
        }
        let period = monitor.period();
        if let Some(mut material) = materials.as_mut().and_then(|m| m.get_mut(&monitor.ecg)) {
            material.params.clock = Vec4::new(
                clock.seconds.rem_euclid(f64::from(period)) as f32,
                if monitor.alive { 1. } else { 0. },
                1.,
                period,
            );
        }
        let at = pose.translation();
        if monitor.alive {
            let phase = (clock.seconds.rem_euclid(f64::from(period)) / f64::from(period)) as f32;
            let crossed = monitor.last_beat >= 0. && monitor.last_beat < R_PHASE && phase >= R_PHASE;
            monitor.last_beat = phase;
            if crossed && can_play && ear.is_some_and(|e| e.distance(at) < AUDIO_RANGE) && config.content_root.join("ecg_beep.ogg").is_file() {
                commands.spawn((
                    Name::new("ECG beep"),
                    AudioPlayer::new(assets.load("ecg_beep.ogg")),
                    PlaybackSettings::DESPAWN
                        .with_spatial(true)
                        .with_spatial_scale(SpatialScale::new(0.5 / AUDIO_RANGE))
                        .with_volume(Volume::Linear(gain(at))),
                    Transform::from_translation(at),
                ));
            }
        } else if ear.is_some_and(|e| e.distance(at) < AUDIO_RANGE) {
            flat_wanted.insert(entity, at);
        }
    }
    for (voice, flat, mut playback, sink) in &mut flats {
        let Some(at) = flat_wanted.remove(&flat.monitor).filter(|_| flat.epoch == epoch.0) else {
            commands.entity(voice).try_despawn();
            continue;
        };
        playback.volume = Volume::Linear(gain(at));
        if let Some(mut sink) = sink {
            sink.set_volume(Volume::Linear(gain(at)));
        }
    }
    if !can_play || !config.content_root.join("ecg_flat.wav").is_file() {
        return;
    }
    for (monitor, at) in flat_wanted {
        commands.spawn((
            Name::new("ECG flatline"),
            Flatline { monitor, epoch: epoch.0 },
            AudioPlayer::new(assets.load("ecg_flat.wav")),
            PlaybackSettings::LOOP
                .with_spatial(true)
                .with_spatial_scale(SpatialScale::new(0.5 / AUDIO_RANGE))
                .with_volume(Volume::SILENT),
            Transform::from_translation(at),
        ));
    }
}
