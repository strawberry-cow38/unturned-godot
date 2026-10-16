//! `RadioDevice` on `Radio_0`/`Radio_1`: F flips the set (OFF by default), the
//! TV's snow hisses while it is on AND fed (`HasFeed`: the mains; its own plug
//! cannot be wired over the server, as for every NetId-0 map fixture), the
//! switch clicks with or without power, and the forecast board floats over a
//! set that is on and fed. Client-local, as in the source.
use super::*;
use crate::character::nameplate::{PlateMaterial, raster::Raster};
use bevy::{
    audio::{SpatialScale, Volume},
    camera::visibility::NoFrustumCulling,
    image::ImageSampler,
    light::{NotShadowCaster, NotShadowReceiver},
    pbr::ExtendedMaterial,
};

/// `StaticVolume` / `StaticUnitSize` / `StaticMaxDist`.
const STATIC_VOLUME: f32 = 0.30;
const STATIC_UNIT: f32 = 1.2;
const STATIC_RANGE: f32 = 6.;
/// `BoardPixelSize`, Label3D FontSize 44, OutlineSize 10, Modulate.
const BOARD_PIXEL: f32 = 0.0016;
const BOARD_FONT: f32 = 44.;
const BOARD_OUTLINE: f32 = 10. / 4.;
const BOARD_TINT: Color = Color::srgb(0.62, 0.90, 1.);

pub(crate) fn is_radio_prop(name: &str) -> bool {
    name == "Radio_0" || name == "Radio_1"
}

#[derive(Component, Debug)]
pub(crate) struct Radio {
    pub(crate) index: usize,
    pub(crate) on: bool,
    center: Vec3,
    board: Vec3,
    /// The board's current text and entity.
    shown: Option<(String, Entity)>,
}
impl Radio {
    /// `Refresh`: the effective state.
    pub(crate) fn playing(&self, mains: bool) -> bool {
        self.on && mains
    }
}

/// `WeatherManager.ForecastLine`: the day's outlook, not what is falling now.
pub(crate) fn forecast(snapshot: &Value) -> Option<String> {
    let weather = &snapshot["weather"];
    let day = |d: &Value| {
        let wet = d["wet"].as_bool().or_else(|| d["Wet"].as_bool())?;
        let name = d["name"].as_str().or_else(|| d["Name"].as_str()).unwrap_or("Clear");
        Some(if wet { name.to_owned() } else { "Clear".to_owned() })
    };
    Some(format!(
        "TODAY  {}\nTOMORROW  {}",
        day(&weather["today"])?,
        day(&weather["tomorrow"])?
    ))
}

pub(crate) fn attach(
    mut commands: Commands,
    config: Res<crate::world::WorldConfig>,
    state: Res<DeviceState>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    objects: Query<(Entity, &WorldObject), (Added<WorldObject>, With<PlacementEuler>)>,
) {
    for (entity, object) in &objects {
        if !is_radio_prop(&object.asset_name) {
            continue;
        }
        let path = config.content_root.join(format!("objects/{}.obj", object.asset_name));
        let Ok((_, data)) = cache.load_mesh(&path, &mut meshes) else {
            continue;
        };
        let center = (data.min + data.max) * 0.5;
        let size = data.max - data.min;
        commands.entity(entity).insert((
            Device(Target::Radio(object.source_index)),
            Radio {
                index: object.source_index,
                on: state.radio_on.contains(&object.source_index),
                center,
                // BuildForecastBoard: the body centre + (0, size.y / 2 + 0.22, 0) in the prop's frame.
                board: center + Vec3::new(0., size.y * 0.5 + 0.22, 0.),
                shown: None,
            },
        ));
    }
}

/// `Toggle`: flip the switch and click, power or not.
#[allow(clippy::too_many_arguments)]
pub(crate) fn press(
    mut commands: Commands,
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    focus: Res<InteractionFocus>,
    config: Res<crate::world::WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
    mut state: ResMut<DeviceState>,
    mut radios: Query<(&Device, &mut Radio, &GlobalTransform)>,
) {
    for (device, mut radio, pose) in &mut radios {
        if !pressed_target(&ui, &settings, &keys, &mouse, &focus, device.0) {
            continue;
        }
        radio.on = !radio.on;
        if radio.on {
            state.radio_on.insert(radio.index);
        } else {
            state.radio_on.remove(&radio.index);
        }
        let path = if radio.on { "sounds/tv_on.wav" } else { "sounds/tv_off.wav" };
        if audio.is_some() && config.content_root.join(path).is_file() {
            // 0.7 linear, unit size 3, max distance 16, at the body centre.
            commands.spawn((
                Name::new("Radio switch"),
                AudioPlayer::new(assets.load(path)),
                PlaybackSettings::DESPAWN
                    .with_spatial(true)
                    .with_spatial_scale(SpatialScale::new(0.5 / 16.))
                    .with_volume(Volume::Linear(0.7)),
                Transform::from_translation(pose.transform_point(radio.center)),
            ));
        }
    }
}

#[derive(Component)]
pub(crate) struct Hiss {
    radio: Entity,
    epoch: u64,
}
/// The snow loop while playing, attenuated as an AudioStreamPlayer3D is.
#[allow(clippy::too_many_arguments)]
pub(crate) fn hiss(
    mut commands: Commands,
    config: Res<crate::world::WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
    settings: Option<Res<Settings>>,
    epoch: Res<WorldSessionEpoch>,
    mains: Res<Mains>,
    listener: Query<&GlobalTransform, With<PlayerCamera>>,
    radios: Query<(Entity, &Radio, &GlobalTransform)>,
    mut voices: Query<(Entity, &Hiss, &mut PlaybackSettings, Option<&mut SpatialAudioSink>)>,
) {
    let master = settings.map_or(1., |s| s.master_volume);
    let ear = listener.single().ok().map(|t| t.translation());
    let mut wanted: HashMap<Entity, Vec3> = radios
        .iter()
        .filter(|(_, r, _)| r.playing(mains.live))
        .map(|(e, r, pose)| (e, pose.transform_point(r.center)))
        .filter(|(_, at)| ear.is_some_and(|ear| at.distance(ear) < STATIC_RANGE))
        .collect();
    let db = 20. * STATIC_VOLUME.log10();
    for (entity, voice, mut playback, sink) in &mut voices {
        let Some(at) = wanted.remove(&voice.radio).filter(|_| voice.epoch == epoch.0) else {
            commands.entity(entity).try_despawn();
            continue;
        };
        let gain = ear.map_or(0., |ear| {
            crate::sound::distance_gain(db, STATIC_UNIT, STATIC_RANGE, at.distance(ear)) * master
        });
        playback.volume = Volume::Linear(gain);
        if let Some(mut sink) = sink {
            sink.set_volume(Volume::Linear(gain));
        }
    }
    let path = "sounds/tv_static.wav";
    if wanted.is_empty() || audio.is_none() || !config.content_root.join(path).is_file() {
        return;
    }
    let clip = assets.load(path);
    for (radio, at) in wanted {
        commands.spawn((
            Name::new("Radio static"),
            Hiss { radio, epoch: epoch.0 },
            AudioPlayer::new(clip.clone()),
            PlaybackSettings::LOOP
                .with_spatial(true)
                .with_spatial_scale(SpatialScale::new(0.5 / STATIC_RANGE))
                .with_volume(Volume::SILENT),
            Transform::from_translation(at),
        ));
    }
}

/// `UpdateForecastBoard`: shown only while on and fed; the text follows the
/// accepted day outlook. A depth-tested camera-facing Label3D.
#[allow(clippy::too_many_arguments)]
pub(crate) fn board(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    mains: Res<Mains>,
    mut raster: Raster,
    mut meshes: ResMut<Assets<Mesh>>,
    materials: Option<ResMut<Assets<PlateMaterial>>>,
    mut radios: Query<(Entity, &mut Radio)>,
) {
    let line = forecast(&snapshot.0);
    let mut materials = materials;
    for (entity, mut radio) in &mut radios {
        let want = radio.playing(mains.live).then(|| line.clone().unwrap_or_default());
        let current = radio.shown.as_ref().map(|(text, _)| text.clone());
        if want == current {
            continue;
        }
        if let Some((_, board)) = radio.shown.take() {
            commands.entity(board).try_despawn();
        }
        let Some(text) = want else { continue };
        if text.is_empty() {
            radio.shown = Some((text, Entity::PLACEHOLDER));
            continue;
        }
        let Some(materials) = materials.as_mut() else { continue };
        let Some(mut bitmap) = raster.render_fitted(&text, BOARD_FONT, f32::MAX, BOARD_OUTLINE, 1.)
        else {
            continue;
        };
        bitmap.image.sampler = ImageSampler::linear();
        let size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32) * BOARD_PIXEL;
        let image = raster.images.add(bitmap.image);
        let board = commands
            .spawn((
                Name::new("Radio forecast board"),
                Mesh3d(meshes.add(Rectangle::from_size(size))),
                MeshMaterial3d(materials.add(ExtendedMaterial {
                    base: StandardMaterial {
                        base_color: BOARD_TINT,
                        base_color_texture: Some(image),
                        alpha_mode: AlphaMode::Blend,
                        unlit: true,
                        double_sided: true,
                        cull_mode: None,
                        ..default()
                    },
                    extension: crate::character::nameplate::Billboard {},
                })),
                Transform::from_translation(radio.board),
                NotShadowCaster,
                NotShadowReceiver,
                NoFrustumCulling,
                ChildOf(entity),
            ))
            .id();
        radio.shown = Some((text, board));
    }
}
