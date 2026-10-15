//! Engine adapter for DayNightCycle.cs and AmbienceAudio.cs. The server owns the
//! clock/weather; only cloud texture motion and audio fades use render time.
use crate::{
    entities::CoreEvent,
    player::{GameSnapshot, PlayerCamera, PlayerUpdate},
    settings::Settings,
    world::WorldConfig,
};
use bevy::{
    asset::embedded_asset,
    audio::{AudioSinkPlayback, Volume},
    camera::visibility::NoFrustumCulling,
    image::{
        ImageAddressMode, ImageFilterMode, ImageLoaderSettings, ImageSampler,
        ImageSamplerDescriptor,
    },
    light::{NotShadowCaster, NotShadowReceiver},
    mesh::MeshVertexBufferLayoutRef,
    pbr::{MaterialPipeline, MaterialPipelineKey},
    prelude::*,
    render::render_resource::{
        AsBindGroup, RenderPipelineDescriptor, ShaderType, SpecializedMeshPipelineError,
    },
    shader::ShaderRef,
};
use std::{
    collections::HashMap,
    f32::consts::{PI, TAU},
};

pub struct EnvironmentPlugin;
impl Plugin for EnvironmentPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "sky.wesl");
        app.add_plugins(MaterialPlugin::<SkyMaterial>::default())
            .init_resource::<Environment>()
            .add_systems(Startup, setup_environment)
            .add_systems(
                Update,
                (
                    adopt_conditions,
                    update_environment,
                    ambience,
                    gun_audio,
                    blast_audio,
                    hand_audio,
                    apply_volume,
                )
                    .chain()
                    .after(PlayerUpdate),
            );
    }
}

#[derive(Resource)]
struct Environment {
    time: f32,
    storm: f32,
    overcast: bool,
    rain: f32,
    wind: f32,
    elapsed: f32,
    offset: Vec2,
    offset2: Vec2,
    time_override: Option<f32>,
    fog: bool,
    ambience_map: String,
    last_reports: HashMap<String, f32>,
}
impl Default for Environment {
    fn default() -> Self {
        Self {
            time: 0.35,
            storm: 0.,
            overcast: false,
            rain: 0.,
            wind: 0.,
            elapsed: 0.,
            offset: Vec2::ZERO,
            offset2: Vec2::ZERO,
            time_override: std::env::var("UG_TIME")
                .ok()
                .and_then(|s| s.parse::<f32>().ok())
                .filter(|v| v.is_finite()),
            fog: std::env::var("UG_NOFOG").as_deref() != Ok("1"),
            ambience_map: String::new(),
            last_reports: default(),
        }
    }
}

#[derive(Component)]
struct Sun;
#[derive(Component)]
pub(crate) struct Sky;
#[derive(Component)]
struct SoundGain(f32);
#[derive(Component)]
struct AmbienceBed {
    day: bool,
    db: f32,
}

const SKY_TOP: [[f32; 3]; 4] = [
    [0.020, 0.071, 0.180],
    [0.878, 0.753, 0.584],
    [0.400, 0.627, 0.808],
    [0.757, 0.188, 0.267],
];
const HORIZON: [[f32; 3]; 4] = [
    [0.078, 0.071, 0.180],
    [0.761, 0.482, 0.176],
    [0.784, 0.784, 0.784],
    [1., 0.341, 0.204],
];
const GROUND: [[f32; 3]; 4] = [
    [0., 0.071, 0.102],
    [0.651, 0.251, 0.098],
    [0.329, 0.518, 0.780],
    [0.216, 0.118, 0.141],
];
const AMBIENT: [[f32; 3]; 4] = [
    [0.098, 0.196, 0.294],
    [0.329, 0.340, 0.345],
    [0.735, 0.625, 0.467],
    [0.561, 0.106, 0.248],
];
const SUN: [[f32; 3]; 4] = [
    [0., 0., 0.],
    [0.718, 0.463, 0.098],
    [0.933, 0.863, 0.757],
    [1., 0., 0.],
];

fn gradient(keys: &[[f32; 3]; 4], time: f32) -> Vec3 {
    let f = time.rem_euclid(1.) * 4.;
    let i = f.floor() as usize;
    Vec3::from_array(keys[i]).lerp(Vec3::from_array(keys[(i + 1) % 4]), f.fract())
}
fn color(v: Vec3) -> Color {
    Color::srgb(v.x, v.y, v.z)
}
fn linear(v: Vec3) -> Vec4 {
    color(v).to_linear().to_vec4()
}

#[derive(Clone, Copy)]
struct Lighting {
    top: Vec3,
    horizon: Vec3,
    ground: Vec3,
    ambient: Vec3,
    sun: Vec3,
    rotation: Quat,
    direction: Vec3,
    energy: f32,
    fog_color: Vec3,
    fog_density: f32,
    cloud_ambient: f32,
    rim: Vec3,
}
fn lighting(time: f32, storm: f32, overcast: bool) -> Lighting {
    let time = time.rem_euclid(1.);
    let storm = storm.clamp(0., 1.);
    let elevation = -(time * TAU).cos() * 90.;
    let rotation = Quat::from_euler(
        EulerRot::YXZ,
        -40_f32.to_radians(),
        -elevation.to_radians(),
        0.,
    );
    let direction = rotation * Vec3::NEG_Z;
    let day = daylight(time);
    let sl = 0.06 + 0.94 * day;
    let horizon = gradient(&HORIZON, time);
    // Weather deliberately leaves sunlight/ambient alone in the source port.
    Lighting {
        top: gradient(&SKY_TOP, time).lerp(Vec3::new(0.35, 0.39, 0.45) * sl, storm),
        horizon: horizon.lerp(Vec3::new(0.45, 0.48, 0.53) * sl, storm),
        ground: gradient(&GROUND, time),
        ambient: gradient(&AMBIENT, time),
        sun: gradient(&SUN, time),
        rotation,
        direction,
        energy: ((time * TAU - PI * 0.5).sin() * 1.35).clamp(0.015, 1.35),
        fog_color: horizon.lerp(Vec3::new(0.50, 0.54, 0.60) * sl, storm),
        fog_density: (0.003 + (0.0004 - 0.003) * (1. - (time - 0.5).abs() * 2.))
            * if overcast { 2.4 } else { 1. }
            * (1. - storm)
            + 0.008 * storm,
        cloud_ambient: (0.05 + 0.75 * day) * (1. - 0.68 * storm),
        rim: Vec3::new(0.05, 0.06, 0.10)
            .lerp(Vec3::new(0.8, 0.6, 0.4), day)
            .lerp(Vec3::new(0.22, 0.24, 0.28) * sl, storm),
    }
}

/// DayNightCycle.DaylightFactor, shared by the sky and unshaded rain streaks.
pub(crate) fn daylight(time: f32) -> f32 {
    let elevation = -(time.rem_euclid(1.) * TAU).cos() * std::f32::consts::FRAC_PI_2;
    (elevation.sin() + 0.15).clamp(0., 1.)
}

#[derive(Clone, Debug, ShaderType)]
struct SkyParameters {
    top: Vec4,
    horizon: Vec4,
    ground: Vec4,
    sun: Vec4,
    direction: Vec4,
    rim: Vec4,
    cloud_ambient: Vec4,
    offsets: Vec4,
    factors: Vec4,
    moon: Vec4,
}
impl SkyParameters {
    fn new(light: Lighting, env: &Environment) -> Self {
        Self {
            top: linear(light.top),
            horizon: linear(light.horizon),
            ground: linear(light.ground),
            sun: linear(light.sun),
            direction: light.direction.extend(0.),
            rim: linear(light.rim),
            cloud_ambient: linear(Vec3::splat(light.cloud_ambient)),
            offsets: Vec4::new(env.offset.x, env.offset.y, env.offset2.x, env.offset2.y),
            factors: Vec4::new(
                0.6 - 0.34 * env.storm,
                10. + 5. * env.storm,
                1. + 0.6 * env.storm,
                env.elapsed,
            ),
            moon: linear(Vec3::new(0.749, 0.804, 0.808)),
        }
    }
}
#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
struct SkyMaterial {
    #[uniform(0)]
    parameters: SkyParameters,
    #[texture(1)]
    #[sampler(2)]
    clouds: Handle<Image>,
    #[texture(3)]
    #[sampler(4)]
    stars: Handle<Image>,
}
impl Material for SkyMaterial {
    fn enable_prepass() -> bool {
        // A background sphere must not look like opaque terrain to water's
        // shoreline-depth query (the main pass already disables depth writes).
        false
    }
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/sky.wesl".into()
    }
    fn specialize(
        _: &MaterialPipeline,
        descriptor: &mut RenderPipelineDescriptor,
        _: &MeshVertexBufferLayoutRef,
        _: MaterialPipelineKey<Self>,
    ) -> Result<(), SpecializedMeshPipelineError> {
        descriptor.primitive.cull_mode = None;
        if let Some(depth) = &mut descriptor.depth_stencil {
            depth.depth_write_enabled = Some(false);
        }
        Ok(())
    }
}
fn sky_texture(assets: &AssetServer, path: &'static str) -> Handle<Image> {
    assets
        .load_builder()
        .with_settings(|settings: &mut ImageLoaderSettings| {
            settings.is_srgb = false;
            settings.sampler = ImageSampler::Descriptor(ImageSamplerDescriptor {
                address_mode_u: ImageAddressMode::Repeat,
                address_mode_v: ImageAddressMode::Repeat,
                mag_filter: ImageFilterMode::Linear,
                min_filter: ImageFilterMode::Linear,
                ..default()
            });
        })
        .load(path)
}
fn setup_environment(
    mut commands: Commands,
    assets: Res<AssetServer>,
    env: Res<Environment>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<SkyMaterial>>,
) {
    let light = lighting(env.time, env.storm, env.overcast);
    commands.spawn((
        Sun,
        DirectionalLight {
            illuminance: light.energy * 18000.,
            color: color(light.sun),
            shadow_maps_enabled: true,
            ..default()
        },
        Transform::from_rotation(light.rotation),
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: color(light.ambient),
        brightness: 350.,
        ..default()
    });
    commands.spawn((
        Sky,
        Mesh3d(meshes.add(Sphere::new(1400.).mesh().uv(48, 24))),
        MeshMaterial3d(materials.add(SkyMaterial {
            parameters: SkyParameters::new(light, &env),
            clouds: sky_texture(&assets, "sky_clouds.png"),
            stars: sky_texture(&assets, "sky_stars.png"),
        })),
        Transform::default(),
        NoFrustumCulling,
        NotShadowCaster,
        NotShadowReceiver,
    ));
}
fn adopt_conditions(snapshot: Res<GameSnapshot>, mut env: ResMut<Environment>) {
    if let Some(value) = snapshot.0["worldClock"]["timeOfDay"]
        .as_f64()
        .filter(|n| n.is_finite())
    {
        env.time = value as f32;
    }
    if let Some(value) = env.time_override {
        env.time = value.rem_euclid(1.);
    }
    let weather = &snapshot.0["weather"];
    env.overcast = weather["blendAlpha"].as_f64().unwrap_or(0.) > 0.35;
    env.rain = weather["rainIntensity"].as_f64().unwrap_or(0.) as f32;
    env.storm = env.rain.clamp(0., 1.);
    env.wind = weather["windMain"].as_f64().unwrap_or(0.) as f32;
}
fn cloud_step(env: &mut Environment, dt: f32) {
    if !(0. ..=1.).contains(&dt) {
        return;
    }
    env.elapsed += dt;
    let angle = 1.2_f32.atan2(2.5) + 0.30 * (env.elapsed * 0.06).sin();
    let dir = -Vec2::new(angle.cos(), angle.sin());
    let perpendicular = Vec2::new(dir.y, -dir.x);
    let speed = 1. + 2.4 * env.wind.clamp(0., 1.);
    let morph = 1. + 1.4 * env.storm;
    env.offset = (env.offset + dir * (0.0005 * speed * dt)).rem_euclid(Vec2::ONE);
    env.offset2 = (env.offset2
        + (dir * 0.55 + perpendicular * 0.38) * (0.0005 * speed * morph * dt))
        .rem_euclid(Vec2::ONE);
}
fn update_environment(
    mut commands: Commands,
    time: Res<Time>,
    mut env: ResMut<Environment>,
    mut sun: Query<
        (&mut DirectionalLight, &mut Transform),
        (With<Sun>, Without<Sky>, Without<PlayerCamera>),
    >,
    mut sky: Query<
        (&MeshMaterial3d<SkyMaterial>, &mut Transform),
        (With<Sky>, Without<Sun>, Without<PlayerCamera>),
    >,
    cameras: Query<(Entity, &Transform), (With<PlayerCamera>, Without<Sky>, Without<Sun>)>,
    mut ambient: ResMut<GlobalAmbientLight>,
    mut materials: ResMut<Assets<SkyMaterial>>,
    mut clear: ResMut<ClearColor>,
) {
    cloud_step(&mut env, time.delta_secs());
    let light = lighting(env.time, env.storm, env.overcast);
    clear.0 = color(light.horizon);
    ambient.color = color(light.ambient);
    for (mut sun, mut transform) in &mut sun {
        sun.color = color(light.sun);
        sun.illuminance = light.energy * 18000.;
        transform.rotation = light.rotation;
    }
    for (entity, camera) in &cameras {
        commands.entity(entity).insert((
            DistanceFog {
                color: color(light.fog_color),
                falloff: FogFalloff::Exponential {
                    density: if env.fog { light.fog_density } else { 0. },
                },
                ..default()
            },
            SpatialListener::new(0.2),
        ));
        for (handle, mut transform) in &mut sky {
            transform.translation = camera.translation;
            if let Some(mut mat) = materials.get_mut(handle) {
                mat.parameters = SkyParameters::new(light, &env);
            }
        }
    }
}

fn day_share(time: f32) -> f32 {
    let t = time.rem_euclid(1.);
    if (0.30..=0.70).contains(&t) {
        1.
    } else if (0.20..0.30).contains(&t) {
        (t - 0.20) / 0.10
    } else if (0.70..=0.80).contains(&t) {
        1. - (t - 0.70) / 0.10
    } else {
        0.
    }
}
fn bird_share(time: f32) -> f32 {
    let t = time.rem_euclid(1.);
    let rise = ((t - 0.20) / 0.10).clamp(0., 1.);
    let fall = ((19. / 24. - t) / (1.5 / 24.)).clamp(0., 1.);
    rise.min(fall)
}
fn bird_rain_share(rain: f32) -> f32 {
    let t = ((rain.clamp(0., 1.) - 0.05) / 0.30).clamp(0., 1.);
    1. - t * t * (3. - 2. * t)
}
fn db_gain(db: f32) -> f32 {
    10_f32.powf(db / 20.)
}
fn ambience(
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    time: Res<Time>,
    mut env: ResMut<Environment>,
    mut beds: Query<(Entity, &mut AmbienceBed, &mut SoundGain)>,
) {
    if env.ambience_map != config.map_name {
        for (entity, _, _) in &beds {
            commands.entity(entity).despawn();
        }
        env.ambience_map = config.map_name.clone();
        for (day, suffix) in [(true, "day"), (false, "night")] {
            let specific = format!(
                "audio/ambience/{}_{}.ogg",
                config.map_name.to_lowercase(),
                suffix
            );
            let path = if config.content_root.join(&specific).exists() {
                specific
            } else {
                format!("audio/ambience/pei_{suffix}.ogg")
            };
            if config.content_root.join(&path).exists() {
                commands.spawn((
                    AudioPlayer::new(assets.load(path)),
                    PlaybackSettings::LOOP.with_volume(Volume::Linear(0.)),
                    AmbienceBed { day, db: -80. },
                    SoundGain(0.),
                ));
            }
        }
        return;
    }
    for (_, mut bed, mut gain) in &mut beds {
        let amp = if bed.day {
            bird_share(env.time) * bird_rain_share(env.rain)
        } else {
            (1. - day_share(env.time)) * (1. - env.rain.clamp(0., 1.))
        };
        let target = if amp <= 0.001 {
            -80.
        } else {
            -9. + 20. * amp.clamp(0.0001, 1.).log10()
        };
        bed.db += (target - bed.db) * (0.5 * time.delta_secs()).clamp(0., 1.);
        gain.0 = db_gain(bed.db);
    }
}
fn gun_audio(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    snapshot: Res<GameSnapshot>,
    mut env: ResMut<Environment>,
) {
    for event in events.read().filter(|event| event.kind == "fire") {
        let Some(gun) = event.data["gun"].as_str() else {
            continue;
        };
        if !gun.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'_') {
            continue;
        }
        let local = event.data["playerId"] == snapshot.0["playerId"];
        if !local {
            if env
                .last_reports
                .get(gun)
                .is_some_and(|last| env.elapsed - last < 0.040)
            {
                continue;
            }
            let now = env.elapsed;
            env.last_reports.insert(gun.to_owned(), now);
        }
        let specific = format!("{gun}_shoot.ogg");
        let fallback = if local {
            "eaglefire_shoot.ogg"
        } else {
            "nykorev_shoot.ogg"
        };
        let path = if config.content_root.join(&specific).exists() {
            specific
        } else {
            fallback.to_string()
        };
        if !config.content_root.join(&path).exists() {
            continue;
        }
        let Some(position) = crate::player::vector(&event.data["origin"]) else {
            continue;
        };
        commands.spawn((
            AudioPlayer::new(assets.load(path)),
            PlaybackSettings::DESPAWN
                .with_spatial(!local)
                .with_spatial_scale(bevy::audio::SpatialScale::new(1. / 34.))
                .with_volume(Volume::Linear(0.)),
            Transform::from_translation(position),
            SoundGain(db_gain(if local { -3. } else { 9. })),
        ));
    }
}
fn blast_audio(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    catalog: Res<crate::entities::ItemCatalog>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
) {
    for event in events.read().filter(|e| e.kind == "grenadeExploded") {
        let id = event.data["itemId"].as_u64().unwrap_or(254) as u16;
        if catalog
            .items
            .get(&id)
            .is_some_and(|i| crate::projectiles::effects::throwable_kind(&i.throwable["kind"]) != 0)
        {
            continue;
        }
        let Some(at) = crate::player::vector(&event.data["pos"]) else {
            continue;
        };
        let radius = event.data["radius"].as_f64().unwrap_or(8.) as f32;
        let path = format!("audio/explosions/bomb_fire_{:02}.wav", event.sequence % 7);
        if !config.content_root.join(&path).is_file() {
            continue;
        }
        let mut seed = event.sequence.max(1);
        let pitch = 0.95 + 0.1 * crate::particles::random(&mut seed);
        let node = crate::vehicle::spawn_ranged_audio(
            &mut commands,
            &assets,
            None,
            &path,
            false,
            db_gain((-2. + radius * 0.6).clamp(-2., 6.)),
            pitch,
            12.,
            260.,
            settings.master_volume,
        );
        commands
            .entity(node)
            .insert(Transform::from_translation(at));
    }
}
fn hand_audio(
    mut commands: Commands,
    mut events: MessageReader<crate::use_items::HandEvent>,
    catalog: Res<crate::entities::ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
) {
    use crate::use_items::HandEvent;
    for event in events.read() {
        let path = match event {
            HandEvent::Start(id) => catalog
                .items
                .get(id)
                .filter(|i| i.is_consumable)
                .and_then(|i| i.consume_animation["sound"].as_str())
                .map(|s| format!("sounds/{s}.wav")),
            HandEvent::Released(id) => {
                throwable_stem(*id).map(|s| format!("audio/items/throwables_{s}_use.ogg"))
            }
            _ => None,
        };
        if let Some(path) = path.filter(|p| config.content_root.join(p).is_file()) {
            commands.spawn((
                AudioPlayer::new(assets.load(path)),
                PlaybackSettings::DESPAWN,
                SoundGain(db_gain(-4.)),
            ));
        }
    }
}
pub fn throwable_stem(id: u16) -> Option<&'static str> {
    Some(match id {
        254 => "grenade",
        1242 => "grenade_makeshift",
        255 => "flare_blue",
        256 => "flare_green",
        257 => "flare_orange",
        258 => "flare_purple",
        259 => "flare_red",
        260 => "flare_yellow",
        261 => "smoke_black",
        262 => "smoke_blue",
        263 => "smoke_green",
        264 => "smoke_orange",
        265 => "smoke_purple",
        266 => "smoke_red",
        267 => "smoke_white",
        268 => "smoke_yellow",
        _ => return None,
    })
}
fn apply_volume(
    settings: Res<Settings>,
    mut sounds: Query<(
        &SoundGain,
        &mut PlaybackSettings,
        Option<&mut AudioSink>,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    for (gain, mut playback, sink, spatial) in &mut sounds {
        let volume = Volume::Linear(gain.0 * settings.master_volume);
        playback.volume = volume;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
        }
        if let Some(mut sink) = spatial {
            sink.set_volume(volume);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_gradient_keys_wrap_and_interpolate() {
        assert_eq!(gradient(&SKY_TOP, 0.), Vec3::from_array(SKY_TOP[0]));
        assert_eq!(gradient(&SKY_TOP, 1.), gradient(&SKY_TOP, 0.));
        assert_eq!(gradient(&SKY_TOP, -0.25), Vec3::from_array(SKY_TOP[3]));
        assert!(
            (gradient(&SKY_TOP, 0.375)
                - (Vec3::from_array(SKY_TOP[1]) + Vec3::from_array(SKY_TOP[2])) * 0.5)
                .length()
                < 1e-6
        );
    }
    #[test]
    fn weather_changes_sky_and_fog_but_preserves_lighting() {
        let clear = lighting(0.5, 0., false);
        let storm = lighting(0.5, 1., true);
        assert_eq!(clear.sun, storm.sun);
        assert_eq!(clear.ambient, storm.ambient);
        assert_eq!(clear.energy, storm.energy);
        assert_ne!(clear.top, storm.top);
        assert!((clear.fog_density - 0.0004).abs() < 1e-6);
        assert!((storm.fog_density - 0.008).abs() < 1e-6);
        assert!(clear.direction.y < -0.99);
        assert!(lighting(0., 0., false).direction.y > 0.99);
    }
    #[test]
    fn birds_stop_at_nineteen_while_day_night_crossfade_is_still_active() {
        assert_eq!(bird_share(19. / 24.), 0.);
        assert_eq!(bird_share(0.5), 1.);
        assert!(day_share(19. / 24.) > 0.);
        assert!((bird_share(18. / 24.) - 2. / 3.).abs() < 1e-6);
        assert_eq!(bird_rain_share(0.05), 1.);
        assert_eq!(bird_rain_share(0.35), 0.);
        assert_eq!(day_share(1.5), 1.);
        assert_eq!(day_share(0.), 0.);
    }
    #[test]
    fn changing_weather_changes_cloud_velocity_without_teleport() {
        let mut env = Environment::default();
        cloud_step(&mut env, 0.02);
        let before = env.offset;
        env.wind = 1.;
        env.storm = 1.;
        cloud_step(&mut env, 0.02);
        assert!((env.offset - before).length() < 0.001);
        let previous = env.offset;
        cloud_step(&mut env, 5.);
        assert_eq!(env.offset, previous);
        assert_ne!(env.offset, env.offset2);
    }
}
