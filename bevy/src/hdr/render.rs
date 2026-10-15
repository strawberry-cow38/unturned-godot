use super::{Composite, HdrSettings};
use bevy::{
    core_pipeline::{
        Core2dSystems, Core3dSystems, FullscreenShader,
        schedule::{Core2d, Core3d},
        tonemapping::tonemapping,
    },
    prelude::*,
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_component::{ComponentUniforms, DynamicUniformIndex},
        render_asset::RenderAssets,
        render_resource::{
            binding_types::{sampler, texture_2d, texture_storage_2d, uniform_buffer},
            *,
        },
        renderer::{RenderContext, RenderDevice, ViewQuery},
        texture::GpuImage,
        view::ViewTarget,
    },
    ui_render::render_pass::ui_pass,
};
use std::collections::HashMap;
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct CompositePass;
pub(super) fn install(app: &mut App) {
    let Some(render) = app.get_sub_app_mut(RenderApp) else {
        return;
    };
    render
        .add_systems(RenderStartup, initialize)
        .add_systems(Render, prepare.in_set(RenderSystems::PrepareBindGroups))
        .add_systems(
            Core3d,
            draw.in_set(Core3dSystems::PostProcess)
                .after(crate::world::underwater::UnderwaterPass)
                .before(tonemapping),
        )
        .add_systems(
            Core2d,
            composite
                .in_set(CompositePass)
                .after(Core2dSystems::PostProcess)
                .before(ui_pass),
        );
}
#[derive(Resource)]
struct Pipelines {
    blur_layout: BindGroupLayoutDescriptor,
    tone_layout: BindGroupLayoutDescriptor,
    composite_layout: BindGroupLayoutDescriptor,
    sampler: Sampler,
    gather: CachedComputePipelineId,
    blur: CachedComputePipelineId,
    tone: Handle<Shader>,
    composite: Handle<Shader>,
    vertex: VertexState,
    formats: HashMap<TextureFormat, (CachedRenderPipelineId, CachedRenderPipelineId)>,
}
fn initialize(
    mut commands: Commands,
    device: Res<RenderDevice>,
    cache: Res<PipelineCache>,
    assets: Res<AssetServer>,
    fullscreen: Res<FullscreenShader>,
) {
    let blur_layout = BindGroupLayoutDescriptor::new(
        "source_glow",
        &BindGroupLayoutEntries::sequential(
            ShaderStages::COMPUTE,
            (
                texture_2d(TextureSampleType::Float { filterable: true }),
                sampler(SamplerBindingType::Filtering),
                texture_storage_2d(TextureFormat::Rgba16Float, StorageTextureAccess::WriteOnly),
                uniform_buffer::<HdrSettings>(true),
            ),
        ),
    );
    let tone_layout = BindGroupLayoutDescriptor::new(
        "source_aces",
        &BindGroupLayoutEntries::sequential(
            ShaderStages::FRAGMENT,
            (
                texture_2d(TextureSampleType::Float { filterable: true }),
                sampler(SamplerBindingType::Filtering),
                texture_2d(TextureSampleType::Float { filterable: true }),
                uniform_buffer::<HdrSettings>(true),
            ),
        ),
    );
    let composite_layout = BindGroupLayoutDescriptor::new(
        "source_viewport_composite",
        &BindGroupLayoutEntries::sequential(
            ShaderStages::FRAGMENT,
            (
                texture_2d(TextureSampleType::Float { filterable: true }),
                sampler(SamplerBindingType::Filtering),
                texture_2d(TextureSampleType::Float { filterable: true }),
            ),
        ),
    );
    let glow = assets.load("embedded://unturned_bevy/hdr/glow.wesl");
    let compute = |entry: &'static str| {
        cache.queue_compute_pipeline(ComputePipelineDescriptor {
            label: Some(format!("source_glow_{entry}").into()),
            layout: vec![blur_layout.clone()],
            shader: glow.clone(),
            entry_point: Some(entry.into()),
            ..default()
        })
    };
    commands.insert_resource(Pipelines {
        gather: compute("gather"),
        blur: compute("blur"),
        blur_layout,
        tone_layout,
        composite_layout,
        sampler: device.create_sampler(&SamplerDescriptor {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::Linear,
            mipmap_filter: MipmapFilterMode::Linear,
            ..default()
        }),
        tone: assets.load("embedded://unturned_bevy/hdr/tonemap.wesl"),
        composite: assets.load("embedded://unturned_bevy/hdr/composite.wesl"),
        vertex: fullscreen.to_vertex_state(),
        formats: default(),
    });
}
#[derive(Component)]
struct GlowChain {
    _texture: Texture,
    size: UVec2,
    all: TextureView,
    levels: Vec<TextureView>,
}
fn chain(device: &RenderDevice, size: UVec2) -> GlowChain {
    let half = (size / 2).max(UVec2::ONE);
    let count = (half.min_element().ilog2() + 1).min(4);
    let texture = device.create_texture(&TextureDescriptor {
        label: Some("source_gaussian_glow"),
        size: Extent3d {
            width: half.x,
            height: half.y,
            depth_or_array_layers: 1,
        },
        mip_level_count: count,
        sample_count: 1,
        dimension: TextureDimension::D2,
        format: TextureFormat::Rgba16Float,
        usage: TextureUsages::TEXTURE_BINDING | TextureUsages::STORAGE_BINDING,
        view_formats: &[],
    });
    let all = texture.create_view(&TextureViewDescriptor::default());
    let levels = (0..count)
        .map(|level| {
            texture.create_view(&TextureViewDescriptor {
                base_mip_level: level,
                mip_level_count: Some(1),
                ..default()
            })
        })
        .collect();
    GlowChain {
        _texture: texture,
        size,
        all,
        levels,
    }
}
fn prepare(
    mut commands: Commands,
    views: Query<(
        Entity,
        &ViewTarget,
        Option<&HdrSettings>,
        Option<&Composite>,
        Option<&GlowChain>,
    )>,
    mut pipelines: ResMut<Pipelines>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
) {
    for (entity, target, settings, composite, old) in &views {
        if settings.is_none() && composite.is_none() {
            continue;
        }
        let format = target.main_texture_format();
        if !pipelines.formats.contains_key(&format) {
            let make = |name: &str, shader: Handle<Shader>, layout: BindGroupLayoutDescriptor| {
                cache.queue_render_pipeline(RenderPipelineDescriptor {
                    label: Some(name.to_owned().into()),
                    layout: vec![layout],
                    vertex: pipelines.vertex.clone(),
                    fragment: Some(FragmentState {
                        shader,
                        targets: vec![Some(ColorTargetState {
                            format,
                            blend: None,
                            write_mask: ColorWrites::ALL,
                        })],
                        ..default()
                    }),
                    ..default()
                })
            };
            let tone = make(
                "source_glow_aces",
                pipelines.tone.clone(),
                pipelines.tone_layout.clone(),
            );
            let composite = make(
                "source_viewmodel_canvas",
                pipelines.composite.clone(),
                pipelines.composite_layout.clone(),
            );
            pipelines.formats.insert(format, (tone, composite));
        }
        if settings.is_some() {
            let extent = target.main_texture().size();
            let size = UVec2::new(extent.width, extent.height);
            if old.is_none_or(|old| old.size != size) {
                commands.entity(entity).insert(chain(&device, size));
            }
        }
    }
}
fn draw(
    view: ViewQuery<(
        &ViewTarget,
        &HdrSettings,
        &DynamicUniformIndex<HdrSettings>,
        &GlowChain,
    )>,
    pipelines: Res<Pipelines>,
    cache: Res<PipelineCache>,
    uniforms: Res<ComponentUniforms<HdrSettings>>,
    mut context: RenderContext,
) {
    let (target, settings, index, chain) = view.into_inner();
    let Some(ids) = pipelines.formats.get(&target.main_texture_format()) else {
        return;
    };
    let Some(tone) = cache.get_render_pipeline(ids.0) else {
        return;
    };
    let Some(uniform) = uniforms.uniforms().binding() else {
        return;
    };
    let compute = if settings.glow.x > 0. {
        let (Some(gather), Some(blur)) = (
            cache.get_compute_pipeline(pipelines.gather),
            cache.get_compute_pipeline(pipelines.blur),
        ) else {
            return;
        };
        Some((gather, blur))
    } else {
        None
    };
    // Check every pipeline before flipping the shared view texture.
    let output = target.post_process_write();
    if let Some((gather, blur)) = compute {
        for (level, dest) in chain.levels.iter().enumerate() {
            let src = if level == 0 {
                output.source
            } else {
                &chain.levels[level - 1]
            };
            let bind = context.render_device().create_bind_group(
                "source_glow",
                &cache.get_bind_group_layout(&pipelines.blur_layout),
                &BindGroupEntries::sequential((src, &pipelines.sampler, dest, uniform.clone())),
            );
            let size = (chain.size / (2u32 << level)).max(UVec2::ONE);
            let mut pass = context
                .command_encoder()
                .begin_compute_pass(&ComputePassDescriptor {
                    label: Some("source_gaussian_glow"),
                    timestamp_writes: None,
                });
            pass.set_pipeline(if level == 0 { gather } else { blur });
            pass.set_bind_group(0, &bind, &[index.index()]);
            pass.dispatch_workgroups(size.x.div_ceil(8), size.y.div_ceil(8), 1);
        }
    }
    let bind = context.render_device().create_bind_group(
        "source_glow_aces",
        &cache.get_bind_group_layout(&pipelines.tone_layout),
        &BindGroupEntries::sequential((output.source, &pipelines.sampler, &chain.all, uniform)),
    );
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_glow_aces"),
            color_attachments: &[Some(RenderPassColorAttachment {
                view: output.destination,
                depth_slice: None,
                resolve_target: None,
                ops: Operations::default(),
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
    pass.set_pipeline(tone);
    pass.set_bind_group(0, &bind, &[index.index()]);
    pass.draw(0..3, 0..1);
}
fn composite(
    view: ViewQuery<(&ViewTarget, &Composite)>,
    pipelines: Res<Pipelines>,
    cache: Res<PipelineCache>,
    images: Res<RenderAssets<GpuImage>>,
    mut context: RenderContext,
) {
    let (target, settings) = view.into_inner();
    if !settings.active {
        return;
    }
    let Some(ids) = pipelines.formats.get(&target.main_texture_format()) else {
        return;
    };
    let Some(pipeline) = cache.get_render_pipeline(ids.1) else {
        return;
    };
    let Some(image) = images.get(&settings.image) else {
        return;
    };
    let output = target.post_process_write();
    let bind = context.render_device().create_bind_group(
        "source_viewmodel_canvas",
        &cache.get_bind_group_layout(&pipelines.composite_layout),
        &BindGroupEntries::sequential((output.source, &pipelines.sampler, &image.texture_view)),
    );
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_viewmodel_canvas"),
            color_attachments: &[Some(RenderPassColorAttachment {
                view: output.destination,
                depth_slice: None,
                resolve_target: None,
                ops: Operations::default(),
            })],
            depth_stencil_attachment: None,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        });
    pass.set_pipeline(pipeline);
    pass.set_bind_group(0, &bind, &[]);
    pass.draw(0..3, 0..1);
}
