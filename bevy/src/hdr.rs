//! Independent source world/viewmodel HDR pipelines and non-HDR canvas composite.
mod light_units;
mod render;
pub(crate) use render::CompositePass;
#[cfg(test)]
mod tests;
use crate::{
    player::PlayerCamera,
    viewmodel::{ViewmodelCamera, scopes::ScopeView},
    world::rain::lightning::ConsoleCamera,
};
use bevy::{
    asset::embedded_asset,
    camera::{CameraUpdateSystems, Hdr, ImageRenderTarget, RenderTarget},
    image::ImageSampler,
    prelude::*,
    render::{
        RenderApp,
        extract_component::{ExtractComponent, ExtractComponentPlugin, UniformComponentPlugin},
        render_resource::{ShaderType, TextureFormat},
    },
    window::PrimaryWindow,
};

/// Godot's nonphysical directional lights multiply energy by PI before the
/// BRDF. Bevy takes lux and then applies the camera's photographic exposure.
pub(crate) fn directional_illuminance(energy: f32) -> f32 {
    energy * std::f32::consts::PI / bevy::camera::Exposure::default().exposure()
}
/// Source flat ambient is color * energy; Bevy also applies camera exposure.
pub(crate) fn ambient_brightness(energy: f32) -> f32 {
    energy / bevy::camera::Exposure::default().exposure()
}
/// Godot's nonphysical omni/spot energy also gains PI. Bevy divides lumens by
/// 4*PI for both light types before camera exposure. This matches the source
/// intensity at one metre; the source's default 1/d falloff is a separate shader
/// adaptation from Bevy's inverse-square attenuation.
pub(crate) fn point_lumens(energy: f32) -> f32 {
    energy * 4. * std::f32::consts::PI.powi(2) / bevy::camera::Exposure::default().exposure()
}

#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct HdrSettings {
    // intensity, below-threshold bloom, threshold, exposure
    glow: Vec4,
    // ACES, clamp output to display range, reserved
    tone: Vec4,
}
#[derive(Component, Clone, ExtractComponent)]
#[extract_app(RenderApp)]
struct Composite {
    image: Handle<Image>,
    active: bool,
}
#[derive(Resource, Default)]
struct ViewmodelTarget {
    image: Option<Handle<Image>>,
    size: UVec2,
}
#[derive(Resource)]
struct SourceSettings {
    enabled: bool,
    aces: bool,
    intensity: f32,
    threshold: f32,
    exposure: f32,
}
impl Default for SourceSettings {
    fn default() -> Self {
        let number = |name: &str, fallback: f32| {
            std::env::var(name)
                .ok()
                .and_then(|v| v.parse::<f32>().ok())
                .filter(|v| v.is_finite())
                .unwrap_or(fallback)
        };
        Self {
            enabled: std::env::var("UG_NOGLOW").as_deref() != Ok("1"),
            aces: std::env::var("UG_LINEAR").as_deref() != Ok("1"),
            intensity: number("UG_GLOW", 0.8),
            threshold: number("UG_GLOWTHRESH", 0.9),
            exposure: number("UG_EXP", 1.),
        }
    }
}
impl SourceSettings {
    fn world(&self, night: bool, military: bool, scope: bool) -> HdrSettings {
        let (intensity, bloom, threshold) = if !night {
            (1., 0.1, 1.)
        } else if military {
            (2.2, 0.45, 0.55)
        } else {
            (1.7, 0.30, 0.70)
        };
        HdrSettings {
            glow: Vec4::new(
                if self.enabled {
                    self.intensity * intensity
                } else {
                    0.
                },
                bloom,
                self.threshold * threshold,
                self.exposure,
            ),
            tone: Vec4::new(
                u8::from(self.aces && !scope) as f32,
                u8::from(!scope) as f32,
                0.,
                0.,
            ),
        }
    }
    fn viewmodel(&self) -> HdrSettings {
        HdrSettings {
            glow: Vec4::new(if self.enabled { 0.9 } else { 0. }, 0.15, 1.25, 1.),
            tone: Vec4::new(u8::from(self.aces) as f32, 1., 0., 0.),
        }
    }
}
pub struct HdrPlugin;
impl Plugin for HdrPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "hdr/glow.wesl");
        embedded_asset!(app, "hdr/tonemap.wesl");
        embedded_asset!(app, "hdr/composite.wesl");
        app.add_plugins((
            ExtractComponentPlugin::<HdrSettings>::default(),
            UniformComponentPlugin::<HdrSettings>::default(),
            ExtractComponentPlugin::<Composite>::default(),
        ))
        .init_resource::<SourceSettings>()
        .init_resource::<ViewmodelTarget>()
        .add_systems(PostUpdate, configure.before(CameraUpdateSystems));
        render::install(app);
        light_units::install(app);
    }
}
fn target(size: UVec2) -> Image {
    let mut image = Image::new_target_texture(size.x, size.y, TextureFormat::Rgba16Float, None);
    image.sampler = ImageSampler::linear();
    image
}
fn configure(
    mut commands: Commands,
    source: Res<SourceSettings>,
    mut target_image: ResMut<ViewmodelTarget>,
    mut images: ResMut<Assets<Image>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    nightvision: Res<crate::nightvision::NightVision>,
    cameras: Query<(
        Entity,
        &Camera,
        Has<PlayerCamera>,
        Has<ViewmodelCamera>,
        Option<&ScopeView>,
        Has<IsDefaultUiCamera>,
        Has<ConsoleCamera>,
        Has<crate::world::water::ReflectionCamera>,
    )>,
) {
    let Ok(window) = windows.single() else { return };
    let size = UVec2::new(
        window.physical_width().max(1),
        window.physical_height().max(1),
    );
    if target_image.size != size {
        if let Some(handle) = target_image.image.as_ref() {
            if let Some(mut image) = images.get_mut(handle) {
                *image = target(size);
            }
        } else {
            target_image.image = Some(images.add(target(size)));
        }
        target_image.size = size;
    }
    let Some(image) = target_image.image.clone() else {
        return;
    };
    let active = cameras
        .iter()
        .any(|(_, cam, _, vm, _, _, _, _)| vm && cam.is_active);
    for (entity, _, main, vm, scope, ui, console, reflection) in &cameras {
        if !(main || vm || scope.is_some() || ui || console || reflection) {
            continue;
        }
        commands.entity(entity).insert((
            Hdr,
            bevy::core_pipeline::tonemapping::Tonemapping::None,
            bevy::core_pipeline::tonemapping::DebandDither::Disabled,
        ));
        if vm {
            commands.entity(entity).insert((
                source.viewmodel(),
                RenderTarget::Image(ImageRenderTarget {
                    handle: image.clone(),
                    scale_factor: window.scale_factor(),
                }),
            ));
            // RenderTarget is its own component in Bevy0.20; Camera keeps only
            // clear/order state. Resize and scale preserve hook pixel projection.
            commands
                .entity(entity)
                .entry::<Camera>()
                .and_modify(|mut camera| {
                    camera.clear_color = ClearColorConfig::Custom(Color::NONE)
                });
        } else if main || reflection || scope.is_some_and(|s| s.integral) {
            commands.entity(entity).insert(source.world(
                nightvision.active,
                nightvision.military,
                scope.is_some(),
            ));
        }
        if ui {
            commands.entity(entity).insert(Composite {
                image: image.clone(),
                active,
            });
        }
    }
}
