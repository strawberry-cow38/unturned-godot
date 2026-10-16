//! GraphicsOptions.Apply*: the shared settings model reaches the live renderer,
//! window and presentation services. Menus only edit `Settings`; this plugin owns
//! every side effect, as the source keeps state + Apply in one model class so the
//! main-menu and pause views cannot disagree.
mod chromatic;
#[cfg(test)]
mod tests;

use crate::settings::{AntiAliasing, Fullscreen, Graphics, Quality, Settings};
use bevy::{
    anti_alias::{fxaa::Fxaa, taa::TemporalAntiAliasing},
    camera::RenderTarget,
    light::{CascadeShadowConfigBuilder, DirectionalLightShadowMap, ShadowFilteringMethod},
    pbr::ScreenSpaceAmbientOcclusion,
    prelude::*,
    render::{render_resource::TextureFormat, renderer::RenderAdapter, view::Msaa},
    window::{MonitorSelection, PrimaryWindow, VideoModeSelection, WindowMode},
};

/// Values the running process booted with. Rows the source marks "(restart)"
/// compare the saved preference against these, like Godot's override.cfg keys.
#[derive(Resource, Clone, Copy, Debug)]
pub struct Boot {
    pub render_thread: bool,
    pub fog_quality: u8,
    /// `--fixed-fps` movie mode owns frame pacing; the limiter stays out of it.
    pub fixed_fps: bool,
}
impl Boot {
    pub fn from(settings: &Settings, fixed_fps: bool) -> Self {
        Self {
            render_thread: settings.graphics.render_thread,
            fog_quality: settings.graphics.fog_quality,
            fixed_fps,
        }
    }
}

/// What actually reached the renderer, for labels, tests and capture sidecars.
#[derive(Resource, Clone, Debug, Default, PartialEq, serde::Serialize)]
pub struct Applied {
    pub msaa: u32,
    pub fxaa: bool,
    pub taa: bool,
    pub ssao: bool,
    pub sun_shadows: bool,
    pub shadow_map: usize,
    pub shadow_distance: f32,
    pub ui_scale: f32,
    pub target_fps: u32,
    pub bloom: bool,
    pub chromatic: Option<f32>,
    pub reflection_every: Option<u32>,
    pub effect_multiplier: f32,
    pub window_mode: String,
    /// The adapter refused this many samples; the nearest supported count ran.
    pub msaa_fallback: Option<u32>,
}

#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct GraphicsApply;

pub struct GraphicsPlugin;
impl Plugin for GraphicsPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Applied>()
            .init_resource::<Pacing>()
            .add_systems(Startup, capture_reflection_env)
            .add_observer(new_window_camera)
            .add_systems(
                PostUpdate,
                (window, presentation, cameras, sun)
                    .in_set(GraphicsApply)
                    .before(bevy::camera::CameraUpdateSystems),
            )
            .add_systems(Last, limit_frame_rate);
        chromatic::install(app);
    }
}

/// ApplyWindow + ApplyResolution. Native resolution leaves a user-sized window
/// alone; a fullscreen window is never resized under the compositor.
fn window(
    settings: Res<Settings>,
    mut windows: Query<&mut Window, With<PrimaryWindow>>,
    mut applied: ResMut<Applied>,
    mut last: Local<Option<(Fullscreen, [u32; 2])>>,
) {
    let g = &settings.graphics;
    let Ok(mut window) = windows.single_mut() else {
        return;
    };
    if *last == Some((g.fullscreen, g.resolution)) {
        return;
    }
    *last = Some((g.fullscreen, g.resolution));
    let mode = window_mode(g.fullscreen);
    if window.mode != mode {
        window.mode = mode;
    }
    if g.fullscreen == Fullscreen::Windowed && g.resolution != [0, 0] {
        window
            .resolution
            .set(g.resolution[0] as f32, g.resolution[1] as f32);
    }
    applied.window_mode = format!("{:?}", window.mode);
}
pub fn window_mode(mode: Fullscreen) -> WindowMode {
    match mode {
        Fullscreen::Windowed => WindowMode::Windowed,
        Fullscreen::Borderless => WindowMode::BorderlessFullscreen(MonitorSelection::Current),
        Fullscreen::Exclusive => {
            WindowMode::Fullscreen(MonitorSelection::Current, VideoModeSelection::Current)
        }
    }
}

#[derive(Resource)]
struct ReflectionEnv {
    enabled: bool,
    every: Option<u32>,
}
fn capture_reflection_env(mut commands: Commands) {
    // UG_REFLECT/UG_REFLEVERY stay benchmark ablations over the saved row,
    // as the source's UG_NOREFLECT does: unset changes nothing.
    commands.insert_resource(ReflectionEnv {
        enabled: std::env::var("UG_REFLECT").as_deref() != Ok("0"),
        every: std::env::var("UG_REFLEVERY")
            .ok()
            .and_then(|s| s.parse::<u32>().ok())
            .map(|v| v.max(1)),
    });
}

/// UI scale, bloom, effects, water mirror and lens settings: plain values that
/// existing consumers already read every frame or at emitter construction.
fn presentation(
    settings: Res<Settings>,
    mut ui_scale: ResMut<UiScale>,
    mut water: Option<ResMut<crate::world::water::WaterSettings>>,
    reflection: Option<Res<ReflectionEnv>>,
    mut hdr: Option<ResMut<crate::hdr::SourceSettings>>,
    mut particles: Option<ResMut<crate::particles::ParticleAssets>>,
    mut applied: ResMut<Applied>,
) {
    let g = &settings.graphics;
    if ui_scale.0 != g.ui_scale {
        ui_scale.0 = g.ui_scale;
    }
    applied.ui_scale = g.ui_scale;
    applied.target_fps = g.target_fps;
    if let Some(hdr) = hdr.as_mut() {
        if hdr.menu_bloom != g.bloom {
            hdr.menu_bloom = g.bloom;
        }
        applied.bloom = hdr.glow_enabled();
    }
    let multiplier = g.effect_quality.effect_multiplier();
    if let Some(particles) = particles.as_mut() {
        if particles.quality != multiplier {
            particles.quality = multiplier;
        }
    }
    applied.effect_multiplier = multiplier;
    if let (Some(water), Some(env)) = (water.as_mut(), reflection) {
        let enabled = env.enabled && g.planar_reflection != Quality::Off;
        let every = env.every.unwrap_or(g.planar_reflection.planar_every());
        if water.enabled != enabled {
            water.enabled = enabled;
        }
        if water.every_frames != every {
            water.every_frames = every;
        }
        applied.reflection_every = enabled.then_some(every);
    }
    applied.chromatic =
        (g.chromatic_aberration && g.chromatic_amount > 0.001).then_some(g.chromatic_amount);
}

/// A window camera spawned mid-frame adopts the shared sample count at once,
/// so it never renders one frame at Bevy's default 4x beside the others.
fn new_window_camera(
    add: On<Add<Camera>>,
    cameras: Query<&RenderTarget, Without<crate::viewmodel::ViewmodelCamera>>,
    applied: Res<Applied>,
    mut commands: Commands,
) {
    if applied.msaa == 0 {
        return;
    }
    if let Ok(RenderTarget::Window(_)) = cameras.get(add.entity) {
        commands.entity(add.entity).insert(Msaa::from_samples(applied.msaa));
    }
}

/// The sample count every window camera must share, after the adapter check.
pub fn msaa_samples(mode: AntiAliasing, supported: impl Fn(u32) -> bool) -> (u32, Option<u32>) {
    let wanted = mode.samples();
    if wanted == 1 || supported(wanted) {
        return (wanted, None);
    }
    // Fall back to the next lower supported count rather than crashing wgpu.
    let fallback = [8, 4, 2]
        .into_iter()
        .filter(|n| *n < wanted)
        .find(|n| supported(*n))
        .unwrap_or(1);
    (fallback, Some(wanted))
}

/// ApplyAA (+ the AO half of ApplyEnvironment). All window cameras share one
/// sample count so the shared view target is never split; image targets
/// (viewmodel, optics, mirror, portrait) keep their own.
fn cameras(
    mut commands: Commands,
    settings: Res<Settings>,
    adapter: Option<Res<RenderAdapter>>,
    world: Query<
        (
            Entity,
            Has<Fxaa>,
            Has<TemporalAntiAliasing>,
            Has<ScreenSpaceAmbientOcclusion>,
            Option<&ShadowFilteringMethod>,
        ),
        With<crate::player::PlayerCamera>,
    >,
    window_cameras: Query<
        (Entity, &RenderTarget, Option<&Msaa>),
        (With<Camera>, Without<crate::viewmodel::ViewmodelCamera>),
    >,
    mut applied: ResMut<Applied>,
) {
    let g = &settings.graphics;
    let supported = |n: u32| {
        adapter.as_ref().is_none_or(|a| {
            [TextureFormat::Rgba16Float, TextureFormat::Depth32Float]
                .into_iter()
                .all(|f| a.get_texture_format_features(f).flags.sample_count_supported(n))
        })
    };
    let (samples, fallback) = msaa_samples(g.anti_aliasing, supported);
    let msaa = Msaa::from_samples(samples);
    for (entity, target, current) in &window_cameras {
        if !matches!(target, RenderTarget::Window(_)) {
            continue;
        }
        if current != Some(&msaa) {
            commands.entity(entity).insert(msaa);
        }
    }
    let fxaa = g.anti_aliasing == AntiAliasing::Fxaa;
    let taa = g.anti_aliasing == AntiAliasing::Taa;
    // Bevy's SSAO requires a non-multisampled main pass.
    let ssao = g.ambient_occlusion && samples == 1;
    let filter = shadow_filter(g.shadows);
    for (entity, has_fxaa, has_taa, has_ssao, current_filter) in &world {
        let mut camera = commands.entity(entity);
        match (fxaa, has_fxaa) {
            (true, false) => {
                camera.insert(Fxaa::default());
            }
            (false, true) => {
                camera.remove::<Fxaa>();
            }
            _ => {}
        }
        match (taa, has_taa) {
            (true, false) => {
                camera.insert(TemporalAntiAliasing::default());
            }
            (false, true) => {
                // Its required jitter and -1 mip bias must not outlive it.
                camera.remove::<(
                    TemporalAntiAliasing,
                    bevy::render::camera::TemporalJitter,
                    bevy::render::camera::MipBias,
                    bevy::core_pipeline::prepass::MotionVectorPrepass,
                )>();
            }
            _ => {}
        }
        match (ssao, has_ssao) {
            (true, false) => {
                camera.insert(ScreenSpaceAmbientOcclusion::default());
            }
            (false, true) => {
                camera.remove::<ScreenSpaceAmbientOcclusion>();
            }
            _ => {}
        }
        if current_filter != Some(&filter) {
            camera.insert(filter);
        }
    }
    applied.msaa = samples;
    applied.msaa_fallback = fallback;
    applied.fxaa = fxaa;
    applied.taa = taa;
    applied.ssao = ssao;
}

/// ApplyShadows' soft filter quality on the native shadow sampler.
pub fn shadow_filter(quality: Quality) -> ShadowFilteringMethod {
    match quality {
        Quality::Off | Quality::Low => ShadowFilteringMethod::Hardware2x2,
        _ => ShadowFilteringMethod::Gaussian,
    }
}

/// ApplyShadows + ApplyShadowDistance on the world's sun.
fn sun(
    mut commands: Commands,
    settings: Res<Settings>,
    mut suns: Query<(Entity, &mut DirectionalLight), With<crate::environment::Sun>>,
    mut map: ResMut<DirectionalLightShadowMap>,
    mut applied: ResMut<Applied>,
    mut last: Local<Option<(Quality, f32, usize)>>,
) {
    let g: &Graphics = &settings.graphics;
    let size = g.shadows.cascade_map_size();
    let distance = std::env::var("UG_SHADOWDIST")
        .ok()
        .and_then(|v| v.parse::<f32>().ok())
        .filter(|v| *v > 0.)
        .unwrap_or(g.shadow_distance);
    let count = suns.iter().len();
    let key = (g.shadows, distance, count);
    if *last == Some(key) {
        return;
    }
    *last = Some(key);
    if size > 0 && map.size != size {
        map.size = size;
    }
    for (entity, mut light) in &mut suns {
        light.shadow_maps_enabled = size > 0;
        commands.entity(entity).insert(
            CascadeShadowConfigBuilder {
                maximum_distance: distance,
                ..default()
            }
            .build(),
        );
    }
    applied.sun_shadows = size > 0;
    applied.shadow_map = size;
    applied.shadow_distance = distance;
}

#[derive(Resource, Default)]
struct Pacing {
    next: Option<std::time::Instant>,
}

/// Engine.MaxFps: sleep the main loop to the saved cap. Zero is uncapped; the
/// deterministic `--fixed-fps` movie path keeps its own pacing.
fn limit_frame_rate(
    settings: Res<Settings>,
    boot: Option<Res<Boot>>,
    mut pacing: ResMut<Pacing>,
) {
    let fps = settings.graphics.target_fps;
    if fps == 0 || boot.is_some_and(|b| b.fixed_fps) {
        pacing.next = None;
        return;
    }
    let period = std::time::Duration::from_secs_f64(1. / f64::from(fps));
    let now = std::time::Instant::now();
    let deadline = pacing.next.unwrap_or(now);
    if deadline > now {
        std::thread::sleep(deadline - now);
    }
    // Re-anchor after a long stall instead of racing to catch up.
    let base = deadline.max(now.checked_sub(period).unwrap_or(now));
    pacing.next = Some(base + period);
}
