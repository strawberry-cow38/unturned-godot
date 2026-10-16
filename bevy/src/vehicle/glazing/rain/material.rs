use bevy::{
    mesh::MeshVertexBufferLayoutRef,
    pbr::{MaterialPipeline, MaterialPipelineKey},
    prelude::*,
    render::render_resource::{
        AsBindGroup, RenderPipelineDescriptor, ShaderType, SpecializedMeshPipelineError,
    },
    shader::ShaderRef,
};

#[derive(Clone, Debug, Reflect, ShaderType)]
pub(crate) struct GlassUniform {
    pub world_from_local: Mat4,
    pub local_from_world: Mat4,
    pub tint: Vec4,
    pub axis: Vec4,
    /// wetness, rain intensity, owner shelter, original shader TIME.
    pub weather: Vec4,
    /// metallic, roughness, source streak_amount diagnostic, debug_view.
    pub surface: Vec4,
}
impl GlassUniform {
    pub fn new(base: &StandardMaterial, axis: Vec3, world: Mat4, weather: Vec4) -> Self {
        Self {
            world_from_local: world,
            local_from_world: world.inverse(),
            tint: Vec4::from_array(base.base_color.to_linear().to_f32_array()),
            axis: axis.extend(0.),
            weather,
            surface: Vec4::new(
                base.metallic,
                base.perceptual_roughness,
                if std::env::var("UG_RUNNERS").as_deref() == Ok("0") {
                    0.
                } else {
                    1.
                },
                std::env::var("UG_GLASSDEBUGVIEW")
                    .ok()
                    .and_then(|v| v.parse::<u32>().ok())
                    .unwrap_or(0) as f32,
            ),
        }
    }
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
pub(crate) struct GlassMaterial {
    #[uniform(100)]
    pub uniform: GlassUniform,
}
impl Material for GlassMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/vehicle/glazing/rain/glass.wesl".into()
    }
    fn alpha_mode(&self) -> AlphaMode {
        AlphaMode::Blend
    }
    fn reads_view_transmission_texture(&self) -> bool {
        true
    }
    fn enable_prepass() -> bool {
        false
    }
    fn enable_shadows() -> bool {
        false
    }
    fn specialize(
        _: &MaterialPipeline,
        descriptor: &mut RenderPipelineDescriptor,
        _: &MeshVertexBufferLayoutRef,
        _: MaterialPipelineKey<Self>,
    ) -> Result<(), SpecializedMeshPipelineError> {
        descriptor.primitive.cull_mode = None;
        Ok(())
    }
}
