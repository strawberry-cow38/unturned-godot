//! The original isolated SubViewport: body-bound framing, fixed portrait lights
//! following source world levels, and Filmic exactly once before canvas drawing.
use super::{LAYER, Paperdoll, Portrait};
use crate::{rig::RigPlayback, viewmodel::lighting::NearbyLight};
use bevy::{
    asset::embedded_asset,
    camera::{CameraUpdateSystems, Hdr, ImageRenderTarget, RenderTarget, visibility::RenderLayers},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    image::ImageSampler,
    prelude::*,
    render::render_resource::{AsBindGroup, TextureFormat},
    shader::ShaderRef,
};

pub(super) const FOV: f32 = 34.;
#[derive(Component)]
struct PortraitCamera;
#[derive(Component)]
struct PortraitLight(u8);

#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
pub(crate) struct PortraitMaterial {
    #[texture(0)]
    #[sampler(1)]
    image: Handle<Image>,
    #[uniform(2)]
    alpha: f32,
}
impl UiMaterial for PortraitMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/ui/inventory/paperdoll/filmic.wesl".into()
    }
}

/// The dashboard supplies its actual logical viewport size. Resizing replaces
/// the image contents in place, so a retained UI node keeps the same handle.
#[derive(Resource, Default)]
pub(in crate::ui::inventory) struct Surface {
    pub size: UVec2,
    pub material: Option<Handle<PortraitMaterial>>,
    image: Option<Handle<Image>>,
    camera: Option<Entity>,
    allocated: UVec2,
}
pub(in crate::ui::inventory) struct PortraitViewPlugin;
impl Plugin for PortraitViewPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "filmic.wesl");
        app.add_plugins(UiMaterialPlugin::<PortraitMaterial>::default())
            .add_systems(
                PostUpdate,
                material_alpha.after(crate::ui::swoop::SwoopAnimation),
            )
            .init_resource::<Surface>()
            .init_resource::<Lighting>()
            .add_systems(
                PostUpdate,
                configure
                    .after(bevy::transform::TransformSystems::Propagate)
                    .before(CameraUpdateSystems),
            )
            .add_systems(
                PostUpdate,
                match_world
                    .after(configure)
                    .before(bevy::light::SimulationLightSystems::UpdateDirectionalLightCascades),
            );
    }
}

fn target(size: UVec2) -> Image {
    let mut image = Image::new_target_texture(size.x, size.y, TextureFormat::Rgba16Float, None);
    image.sampler = ImageSampler::linear();
    image
}

/// Godot FramePaperdoll reads Body.GetAabb(), not the clothing or held weapon.
/// A large bow/hat therefore never changes the original body's framing.
fn frame(rig: &RigPlayback, pose: &GlobalTransform) -> Option<Transform> {
    let (mut low, mut high) = (f32::INFINITY, f32::NEG_INFINITY);
    for p in &rig.data.mesh.positions {
        let y = pose.transform_point(Vec3::from_array(*p)).y;
        low = low.min(y);
        high = high.max(y);
    }
    let height = high - low;
    if !height.is_finite() || height < 0.1 {
        return None;
    }
    let aim_y = (low + high) * 0.5 + 0.05;
    let distance = height * 1.34 * 0.5 / (FOV.to_radians() * 0.5).tan();
    Some(Transform::from_xyz(0., aim_y, distance).looking_at(Vec3::Y * aim_y, Vec3::Y))
}

#[allow(clippy::too_many_arguments)]
fn configure(
    mut commands: Commands,
    portrait: Res<Portrait>,
    mut surface: ResMut<Surface>,
    mut images: ResMut<Assets<Image>>,
    mut materials: ResMut<Assets<PortraitMaterial>>,
    bodies: Query<(&RigPlayback, &GlobalTransform), (With<Paperdoll>, Without<PortraitCamera>)>,
    mut cameras: Query<
        (&mut Camera, &mut Transform, &mut GlobalTransform),
        (With<PortraitCamera>, Without<Paperdoll>),
    >,
) {
    let active = portrait.active && portrait.root.is_some() && surface.size.min_element() > 0;
    if !active {
        for (mut camera, _, _) in &mut cameras {
            camera.is_active = false;
        }
        return;
    }
    if surface.allocated != surface.size {
        let next = target(surface.size);
        if let Some(handle) = surface.image.as_ref() {
            if let Some(mut image) = images.get_mut(handle) {
                *image = next;
            }
        } else {
            let image = images.add(next);
            surface.material = Some(materials.add(PortraitMaterial {
                image: image.clone(),
                alpha: 1.,
            }));
            surface.image = Some(image);
        }
        surface.allocated = surface.size;
    }
    let pose = portrait
        .root
        .and_then(|root| bodies.get(root).ok())
        .and_then(|(rig, pose)| frame(rig, pose))
        .unwrap_or_else(|| Transform::from_xyz(0., 0.98, 3.8));
    if surface.camera.is_none() {
        let image = surface.image.as_ref().unwrap().clone();
        surface.camera = Some(
            commands
                .spawn((
                    Name::new("Inventory portrait camera"),
                    PortraitCamera,
                    Camera3d::default(),
                    Camera {
                        order: -20,
                        clear_color: ClearColorConfig::Custom(Color::NONE),
                        ..default()
                    },
                    RenderTarget::Image(ImageRenderTarget {
                        handle: image,
                        scale_factor: 1.,
                    }),
                    Projection::Perspective(PerspectiveProjection {
                        fov: FOV.to_radians(),
                        ..default()
                    }),
                    AmbientLight {
                        color: Color::srgb(0.42, 0.42, 0.44),
                        brightness: crate::hdr::ambient_brightness(1.),
                        ..default()
                    },
                    Hdr,
                    Tonemapping::None,
                    DebandDither::Disabled,
                    Msaa::Sample4,
                    RenderLayers::layer(LAYER),
                    pose,
                    GlobalTransform::from(pose),
                ))
                .id(),
        );
        for (index, angles, energy, color) in [
            (0, Vec3::new(-30., 150., 0.), 0.75, Color::WHITE),
            (
                1,
                Vec3::new(-8., -35., 0.),
                0.35,
                Color::srgb(0.88, 0.88, 0.91),
            ),
        ] {
            let transform = Transform::from_rotation(Quat::from_euler(
                EulerRot::YXZ,
                angles.y.to_radians(),
                angles.x.to_radians(),
                angles.z.to_radians(),
            ));
            commands.spawn((
                Name::new(format!("Inventory portrait light {index}")),
                PortraitLight(index),
                DirectionalLight {
                    color,
                    illuminance: crate::hdr::directional_illuminance(energy),
                    shadow_maps_enabled: false,
                    ..default()
                },
                transform,
                GlobalTransform::from(transform),
                RenderLayers::layer(LAYER),
            ));
        }
    }
    for (mut camera, mut transform, mut global) in &mut cameras {
        camera.is_active = true;
        *transform = pose;
        *global = pose.into();
    }
}

#[derive(Resource)]
struct Lighting {
    key: Color,
    key_energy: f32,
    fill: Color,
    fill_energy: f32,
    ambient: Color,
    ambient_energy: f32,
}
impl Default for Lighting {
    fn default() -> Self {
        Self {
            key: Color::WHITE,
            key_energy: 0.75,
            fill: Color::srgb(0.88, 0.88, 0.91),
            fill_energy: 0.35,
            ambient: Color::srgb(0.42, 0.42, 0.44),
            ambient_energy: 1.,
        }
    }
}
impl Lighting {
    fn source(
        &mut self,
        room: Option<&NearbyLight>,
        sun: Option<&DirectionalLight>,
        ambient: Option<&GlobalAmbientLight>,
    ) {
        // Exact source order: room contribution first; an existing Sun then
        // replaces fill energy and world ambient, but retains room fill tint.
        if let Some(room) = room.filter(|r| r.energy > 0.001) {
            let lit = room.energy.min(4.);
            self.fill = self
                .fill
                .to_srgba()
                .mix(&room.color.to_srgba(), (lit * 0.35).min(0.75))
                .into();
            self.fill_energy += lit * 0.30;
            self.ambient = self
                .ambient
                .to_srgba()
                .mix(&room.color.to_srgba(), (lit * 0.3).min(0.6))
                .into();
            self.ambient_energy += lit * 0.12;
        }
        let Some(sun) = sun else { return };
        self.key = sun.color;
        self.key_energy =
            (sun.illuminance / crate::hdr::directional_illuminance(1.) * 0.75).max(0.38);
        self.fill_energy = 0.35 * (self.key_energy / 0.75).clamp(0.5, 1.);
        if let Some(ambient) = ambient {
            self.ambient = ambient.color;
            self.ambient_energy =
                (ambient.brightness / crate::hdr::ambient_brightness(1.)).max(0.45);
        }
    }
}
fn match_world(
    portrait: Res<Portrait>,
    mut state: ResMut<Lighting>,
    room: Option<Res<NearbyLight>>,
    sun: Query<&DirectionalLight, (With<crate::environment::Sun>, Without<PortraitLight>)>,
    ambient: Option<Res<GlobalAmbientLight>>,
    mut lights: Query<(&PortraitLight, &mut DirectionalLight), Without<crate::environment::Sun>>,
    mut cameras: Query<&mut AmbientLight, With<PortraitCamera>>,
) {
    if !portrait.active {
        return;
    }
    state.source(room.as_deref(), sun.single().ok(), ambient.as_deref());
    for (kind, mut light) in &mut lights {
        let (color, energy) = if kind.0 == 0 {
            (state.key, state.key_energy)
        } else {
            (state.fill, state.fill_energy)
        };
        light.color = color;
        light.illuminance = crate::hdr::directional_illuminance(energy);
    }
    for mut camera in &mut cameras {
        camera.color = state.ambient;
        camera.brightness = crate::hdr::ambient_brightness(state.ambient_energy);
    }
}

#[cfg(test)]
mod tests;

#[cfg(test)]
mod visual;

fn material_alpha(
    nodes: Query<(
        &MaterialNode<PortraitMaterial>,
        &crate::ui::swoop::SwoopAlpha,
    )>,
    mut materials: ResMut<Assets<PortraitMaterial>>,
) {
    for (node, alpha) in &nodes {
        if let Some(mut material) = materials.get_mut(&node.0) {
            material.alpha = alpha.0;
        }
    }
}
