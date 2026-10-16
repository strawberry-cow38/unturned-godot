//! InfoBillboard.cs: one active 320x296 source canvas on a depth-ignoring,
//! camera-facing world quad. Focus has a single winner, so the viewport is shared.
mod canvas;
#[cfg(test)]
mod tests;
#[cfg(test)]
mod visual;

use crate::{character::nameplate::raster::Raster, world::WorldConfig};
use bevy::{
    camera::visibility::NoFrustumCulling,
    image::ImageSampler,
    light::{NotShadowCaster, NotShadowReceiver},
    mesh::MeshVertexBufferLayoutRef,
    pbr::{ExtendedMaterial, MaterialExtension, MaterialExtensionKey, MaterialExtensionPipeline},
    prelude::*,
    render::render_resource::{
        AsBindGroup, CompareFunction, RenderPipelineDescriptor, SpecializedMeshPipelineError,
    },
    shader::ShaderRef,
    text::TextPipeline,
};

const WIDTH: usize = 320;
const HEIGHT: usize = 296;
const PIXEL_SIZE: f32 = 0.0042;

#[derive(Clone, Copy, PartialEq)]
pub(crate) struct Bar {
    pub value: f32,
    pub color: Color,
}
#[derive(Clone, PartialEq)]
pub(crate) struct Content {
    pub name: String,
    pub name_color: Color,
    pub prompt: String,
    pub prompt_color: Color,
    pub bars: [Option<Bar>; 5],
}
#[derive(Clone)]
pub(crate) struct View {
    pub position: Vec3,
    pub content: Content,
}
#[derive(Resource, Default)]
pub(crate) struct FocusedInfo(pub Option<View>);
#[derive(Resource, Default)]
struct Surface {
    entity: Option<Entity>,
    image: Option<Handle<Image>>,
    content: Option<Content>,
}
#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
struct Face {}
type PanelMaterial = ExtendedMaterial<StandardMaterial, Face>;
impl MaterialExtension for Face {
    fn vertex_shader() -> ShaderRef {
        // The original avatar nameplate already implements a per-view billboard.
        "embedded://unturned_bevy/character/nameplate/billboard.wesl".into()
    }
    fn enable_prepass() -> bool {
        false
    }
    fn specialize(
        _: &MaterialExtensionPipeline,
        descriptor: &mut RenderPipelineDescriptor,
        _: &MeshVertexBufferLayoutRef,
        _: MaterialExtensionKey<Self>,
    ) -> Result<(), SpecializedMeshPipelineError> {
        if let Some(depth) = &mut descriptor.depth_stencil {
            depth.depth_compare = Some(CompareFunction::Always);
            depth.depth_write_enabled = Some(false);
        }
        Ok(())
    }
}
pub(crate) struct InfoBillboardPlugin;
impl Plugin for InfoBillboardPlugin {
    fn build(&self, app: &mut App) {
        crate::character::nameplate::ensure_billboard_shader(app);
        app.init_resource::<FocusedInfo>()
            .init_resource::<Surface>()
            .init_resource::<canvas::Cache>()
            .add_plugins(MaterialPlugin::<PanelMaterial>::default())
            .add_systems(Update, reset.before(crate::entities::CollectInteractions))
            .add_systems(
                Update,
                render
                    .after(crate::entities::ApplyInteractions)
                    .run_if(resource_exists::<TextPipeline>),
            );
    }
}
fn reset(mut info: ResMut<FocusedInfo>) {
    info.0 = None;
}
fn render(
    mut commands: Commands,
    info: Res<FocusedInfo>,
    config: Res<WorldConfig>,
    mut surface: ResMut<Surface>,
    mut cache: ResMut<canvas::Cache>,
    mut raster: Raster,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<PanelMaterial>>,
) {
    let Some(view) = &info.0 else {
        if let Some(entity) = surface.entity {
            commands.entity(entity).insert(Visibility::Hidden);
        }
        return;
    };
    if surface.content.as_ref() != Some(&view.content) {
        let Some(mut image) =
            canvas::compose(&view.content, &config.content_root, &mut raster, &mut cache)
        else {
            return;
        };
        image.sampler = ImageSampler::nearest();
        if let Some(handle) = &surface.image {
            *raster.images.get_mut(handle).unwrap() = image;
        } else {
            surface.image = Some(raster.images.add(image));
        }
        surface.content = Some(view.content.clone());
    }
    let transform = Transform::from_translation(view.position);
    if let Some(entity) = surface.entity {
        commands
            .entity(entity)
            .insert((transform, Visibility::Inherited));
    } else {
        surface.entity = Some(
            commands
                .spawn((
                    Name::new("Original look-at InfoBillboard"),
                    Mesh3d(meshes.add(Rectangle::new(
                        WIDTH as f32 * PIXEL_SIZE,
                        HEIGHT as f32 * PIXEL_SIZE,
                    ))),
                    MeshMaterial3d(materials.add(ExtendedMaterial {
                        base: StandardMaterial {
                            base_color_texture: surface.image.clone(),
                            alpha_mode: AlphaMode::Blend,
                            unlit: true,
                            double_sided: true,
                            cull_mode: None,
                            ..default()
                        },
                        extension: Face {},
                    })),
                    transform,
                    Visibility::Inherited,
                    NotShadowCaster,
                    NotShadowReceiver,
                    NoFrustumCulling,
                ))
                .id(),
        );
    }
}
