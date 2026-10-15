use super::Settings;
use bevy::{
    core_pipeline::{Core2dSystems, FullscreenShader, schedule::Core2d},
    prelude::*,
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_component::{ComponentUniforms, DynamicUniformIndex},
        render_resource::{
            binding_types::{sampler, texture_2d, texture_storage_2d, uniform_buffer},
            *,
        },
        renderer::{RenderContext, RenderDevice, ViewQuery},
        view::ViewTarget,
    },
    ui_render::render_pass::ui_pass,
};
use std::collections::HashMap;
pub(super) fn install(app: &mut App) {
    let Some(render) = app.get_sub_app_mut(RenderApp) else {
        return;
    };
    render
        .add_systems(RenderStartup, initialize)
        .add_systems(Render, prepare.in_set(RenderSystems::PrepareBindGroups))
        .add_systems(
            Core2d,
            draw.after(Core2dSystems::PostProcess).before(ui_pass),
        );
}
#[derive(Resource)]
struct Pipelines {
    compute_layout: BindGroupLayoutDescriptor,
    composite_layout: BindGroupLayoutDescriptor,
    sampler: Sampler,
    copy: CachedComputePipelineId,
    gaussian: CachedComputePipelineId,
    composite: Handle<Shader>,
    vertex: VertexState,
    formats: HashMap<TextureFormat, CachedRenderPipelineId>,
}
fn initialize(
    mut commands: Commands,
    device: Res<RenderDevice>,
    cache: Res<PipelineCache>,
    assets: Res<AssetServer>,
    fullscreen: Res<FullscreenShader>,
) {
    let compute_layout = BindGroupLayoutDescriptor::new(
        "source_canvas_mips",
        &BindGroupLayoutEntries::sequential(
            ShaderStages::COMPUTE,
            (
                texture_2d(TextureSampleType::Float { filterable: true }),
                sampler(SamplerBindingType::Filtering),
                texture_storage_2d(TextureFormat::Rgba8Unorm, StorageTextureAccess::WriteOnly),
            ),
        ),
    );
    let composite_layout = BindGroupLayoutDescriptor::new(
        "source_canvas_frost",
        &BindGroupLayoutEntries::sequential(
            ShaderStages::FRAGMENT,
            (
                texture_2d(TextureSampleType::Float { filterable: true }),
                sampler(SamplerBindingType::Filtering),
                texture_2d(TextureSampleType::Float { filterable: true }),
                uniform_buffer::<Settings>(true),
            ),
        ),
    );
    let shader = assets.load("embedded://unturned_bevy/ui/backdrop/mips.wesl");
    let compute = |entry: &'static str| {
        cache.queue_compute_pipeline(ComputePipelineDescriptor {
            label: Some(format!("source_canvas_{entry}").into()),
            layout: vec![compute_layout.clone()],
            shader: shader.clone(),
            entry_point: Some(entry.into()),
            ..default()
        })
    };
    commands.insert_resource(Pipelines {
        copy: compute("screen_copy"),
        gaussian: compute("gaussian"),
        compute_layout,
        composite_layout,
        sampler: device.create_sampler(&SamplerDescriptor {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::Linear,
            mipmap_filter: MipmapFilterMode::Linear,
            ..default()
        }),
        composite: assets.load("embedded://unturned_bevy/ui/backdrop/composite.wesl"),
        vertex: fullscreen.to_vertex_state(),
        formats: default(),
    });
}
#[derive(Component)]
struct ScreenMips {
    _texture: Texture,
    size: UVec2,
    all: TextureView,
    levels: Vec<TextureView>,
}
fn chain(device: &RenderDevice, size: UVec2) -> ScreenMips {
    // AmmoPie samples LOD1.8, so levels0..2 are the complete needed source chain.
    let size = size.max(UVec2::ONE);
    let count = (size.min_element().ilog2() + 1).min(3);
    let texture = device.create_texture(&TextureDescriptor {
        label: Some("source_canvas_srgb_backbuffer"),
        size: Extent3d {
            width: size.x,
            height: size.y,
            depth_or_array_layers: 1,
        },
        mip_level_count: count,
        sample_count: 1,
        dimension: TextureDimension::D2,
        format: TextureFormat::Rgba8Unorm,
        usage: TextureUsages::TEXTURE_BINDING | TextureUsages::STORAGE_BINDING,
        view_formats: &[],
    });
    let all = texture.create_view(&default());
    let levels = (0..count)
        .map(|level| {
            texture.create_view(&TextureViewDescriptor {
                base_mip_level: level,
                mip_level_count: Some(1),
                ..default()
            })
        })
        .collect();
    ScreenMips {
        _texture: texture,
        size,
        all,
        levels,
    }
}
fn prepare(
    mut commands: Commands,
    views: Query<(Entity, &ViewTarget, &Settings, Option<&ScreenMips>)>,
    mut pipelines: ResMut<Pipelines>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
) {
    for (entity, target, _, old) in &views {
        let format = target.main_texture_format();
        if !pipelines.formats.contains_key(&format) {
            let id = cache.queue_render_pipeline(RenderPipelineDescriptor {
                label: Some("source_canvas_frost".into()),
                layout: vec![pipelines.composite_layout.clone()],
                vertex: pipelines.vertex.clone(),
                fragment: Some(FragmentState {
                    shader: pipelines.composite.clone(),
                    targets: vec![Some(ColorTargetState {
                        format,
                        blend: None,
                        write_mask: ColorWrites::ALL,
                    })],
                    ..default()
                }),
                ..default()
            });
            pipelines.formats.insert(format, id);
        }
        let extent = target.main_texture().size();
        let size = UVec2::new(extent.width, extent.height);
        if old.is_none_or(|old| old.size != size) {
            commands.entity(entity).insert(chain(&device, size));
        }
    }
}
fn draw(
    view: ViewQuery<(
        &ViewTarget,
        &Settings,
        &DynamicUniformIndex<Settings>,
        &ScreenMips,
    )>,
    pipelines: Res<Pipelines>,
    cache: Res<PipelineCache>,
    uniforms: Res<ComponentUniforms<Settings>>,
    mut context: RenderContext,
) {
    let (target, settings, index, chain) = view.into_inner();
    if settings.parameters.x <= 0. {
        return;
    }
    let Some(id) = pipelines.formats.get(&target.main_texture_format()) else {
        return;
    };
    let (Some(composite), Some(copy), Some(gaussian), Some(uniform)) = (
        cache.get_render_pipeline(*id),
        cache.get_compute_pipeline(pipelines.copy),
        cache.get_compute_pipeline(pipelines.gaussian),
        uniforms.uniforms().binding(),
    ) else {
        return;
    };
    // No shared-target swap until every pass can execute this frame.
    let output = target.post_process_write();
    for (level, dest) in chain.levels.iter().enumerate() {
        let src = if level == 0 {
            output.source
        } else {
            &chain.levels[level - 1]
        };
        let group = context.render_device().create_bind_group(
            "source_canvas_mips",
            &cache.get_bind_group_layout(&pipelines.compute_layout),
            &BindGroupEntries::sequential((src, &pipelines.sampler, dest)),
        );
        let size = (chain.size / (1u32 << level)).max(UVec2::ONE);
        let mut pass = context
            .command_encoder()
            .begin_compute_pass(&ComputePassDescriptor {
                label: Some("source_canvas_mips"),
                timestamp_writes: None,
            });
        pass.set_pipeline(if level == 0 { copy } else { gaussian });
        pass.set_bind_group(0, &group, &[]);
        pass.dispatch_workgroups(size.x.div_ceil(8), size.y.div_ceil(8), 1);
    }
    let group = context.render_device().create_bind_group(
        "source_canvas_frost",
        &cache.get_bind_group_layout(&pipelines.composite_layout),
        &BindGroupEntries::sequential((output.source, &pipelines.sampler, &chain.all, uniform)),
    );
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_canvas_frost"),
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
    pass.set_pipeline(composite);
    pass.set_bind_group(0, &group, &[index.index()]);
    pass.draw(0..3, 0..1);
}
