//! A per-view mip chain supplies the original shader's distance-dependent blur.
//! Main view textures have one mip, so sampling them directly at LOD 4.2 would
//! silently leave distant objects sharp. The original 3D screen texture captures
//! opaque geometry before transparent rendering; retain that ordering, then
//! composite the submerged view last. All work stops when the eye is dry.
use super::OpticsUniform;
use bevy::{
    core_pipeline::{
        Core3dSystems, FullscreenShader,
        core_3d::{main_opaque_pass_3d, main_transparent_pass_3d},
        prepass::ViewPrepassTextures,
        schedule::Core3d,
        tonemapping::tonemapping,
    },
    prelude::*,
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_component::{ComponentUniforms, DynamicUniformIndex},
        render_resource::{
            binding_types::{sampler, texture_2d, texture_2d_multisampled, uniform_buffer},
            *,
        },
        renderer::{RenderContext, RenderDevice, ViewQuery},
        view::ViewTarget,
    },
    shader::ShaderDefVal,
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
            Core3d,
            (
                copy_opaque
                    .in_set(Core3dSystems::MainPass)
                    .after(main_opaque_pass_3d)
                    .before(main_transparent_pass_3d),
                draw.in_set(Core3dSystems::PostProcess)
                    .in_set(super::UnderwaterPass)
                    .before(tonemapping),
            ),
        );
}

#[derive(Resource)]
struct Pipelines {
    mip_layout: BindGroupLayoutDescriptor,
    effect_layouts: [BindGroupLayoutDescriptor; 2],
    sampler: Sampler,
    effect: Handle<Shader>,
    mip: Handle<Shader>,
    vertex: VertexState,
    mip_pipelines: HashMap<TextureFormat, (CachedRenderPipelineId, CachedRenderPipelineId)>,
    effect_pipelines: HashMap<(TextureFormat, bool), CachedRenderPipelineId>,
}
fn initialize(
    mut commands: Commands,
    device: Res<RenderDevice>,
    assets: Res<AssetServer>,
    fullscreen: Res<FullscreenShader>,
) {
    let mip_layout = BindGroupLayoutDescriptor::new(
        "underwater_mip_layout",
        &BindGroupLayoutEntries::sequential(
            ShaderStages::FRAGMENT,
            (
                texture_2d(TextureSampleType::Float { filterable: true }),
                sampler(SamplerBindingType::Filtering),
            ),
        ),
    );
    let effect_layouts = std::array::from_fn(|i| {
        let depth = if i == 0 {
            texture_2d(TextureSampleType::Depth)
        } else {
            texture_2d_multisampled(TextureSampleType::Depth)
        };
        BindGroupLayoutDescriptor::new(
            "underwater_optics_layout",
            &BindGroupLayoutEntries::sequential(
                ShaderStages::FRAGMENT,
                (
                    texture_2d(TextureSampleType::Float { filterable: true }),
                    sampler(SamplerBindingType::Filtering),
                    uniform_buffer::<OpticsUniform>(true),
                    depth,
                ),
            ),
        )
    });
    commands.insert_resource(Pipelines {
        mip_layout,
        effect_layouts,
        sampler: device.create_sampler(&SamplerDescriptor {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::Linear,
            mipmap_filter: MipmapFilterMode::Linear,
            ..default()
        }),
        effect: assets.load("embedded://unturned_bevy/world/underwater.wesl"),
        mip: assets.load("embedded://unturned_bevy/world/underwater_mip.wesl"),
        vertex: fullscreen.to_vertex_state(),
        mip_pipelines: HashMap::new(),
        effect_pipelines: HashMap::new(),
    });
}

#[derive(Component)]
struct MipChain {
    _texture: Texture,
    size: UVec2,
    format: TextureFormat,
    all: TextureView,
    levels: Vec<TextureView>,
}
fn mip_count(size: UVec2) -> u32 {
    // The source's largest requested LOD is 4.2; six levels allow interpolation
    // between 4 and 5 without clamping it back to a sharper image.
    (size.min_element().max(1).ilog2() + 1).min(6)
}
fn mip_chain(device: &RenderDevice, size: UVec2, format: TextureFormat) -> MipChain {
    let count = mip_count(size);
    let texture = device.create_texture(&TextureDescriptor {
        label: Some("underwater_scattering_mips"),
        size: Extent3d {
            width: size.x,
            height: size.y,
            depth_or_array_layers: 1,
        },
        mip_level_count: count,
        sample_count: 1,
        dimension: TextureDimension::D2,
        format,
        usage: TextureUsages::TEXTURE_BINDING | TextureUsages::RENDER_ATTACHMENT,
        view_formats: &[],
    });
    let all = texture.create_view(&TextureViewDescriptor::default());
    let levels = (0..count)
        .map(|i| {
            texture.create_view(&TextureViewDescriptor {
                base_mip_level: i,
                mip_level_count: Some(1),
                ..default()
            })
        })
        .collect();
    MipChain {
        _texture: texture,
        size,
        format,
        all,
        levels,
    }
}
#[derive(Component)]
struct Bindings {
    a: (TextureViewId, BindGroup),
    b: (TextureViewId, BindGroup),
    mips: Vec<BindGroup>,
    effect: BindGroup,
    copy_pipeline: CachedRenderPipelineId,
    mip_pipeline: CachedRenderPipelineId,
    effect_pipeline: CachedRenderPipelineId,
}
fn prepare(
    mut commands: Commands,
    views: Query<(
        Entity,
        &ViewTarget,
        &ViewPrepassTextures,
        &Msaa,
        &OpticsUniform,
        Option<&MipChain>,
    )>,
    mut pipelines: ResMut<Pipelines>,
    cache: Res<PipelineCache>,
    uniforms: Res<ComponentUniforms<OpticsUniform>>,
    device: Res<RenderDevice>,
) {
    let Some(uniform) = uniforms.uniforms().binding() else {
        return;
    };
    for (entity, target, prepass, msaa, settings, old) in &views {
        if settings.state.w == 0. {
            continue;
        }
        let Some(depth) = prepass.depth_only_view() else {
            continue;
        };
        let format = target.main_texture_format();
        let texture_size = target.main_texture().size();
        let size = UVec2::new(texture_size.width, texture_size.height);
        if size.min_element() == 0 {
            continue;
        }
        let new = if old.is_none_or(|old| old.size != size || old.format != format) {
            Some(mip_chain(&device, size, format))
        } else {
            None
        };
        let chain = new.as_ref().or(old).unwrap();
        if !pipelines.mip_pipelines.contains_key(&format) {
            let make = |entry: &str| {
                cache.queue_render_pipeline(RenderPipelineDescriptor {
                    label: Some(format!("underwater_{entry}").into()),
                    layout: vec![pipelines.mip_layout.clone()],
                    vertex: pipelines.vertex.clone(),
                    fragment: Some(FragmentState {
                        shader: pipelines.mip.clone(),
                        entry_point: Some(entry.to_owned().into()),
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
            let ids = (make("copy"), make("fragment"));
            pipelines.mip_pipelines.insert(format, ids);
        }
        let multi = msaa.samples() > 1;
        let key = (format, multi);
        if !pipelines.effect_pipelines.contains_key(&key) {
            let id = cache.queue_render_pipeline(RenderPipelineDescriptor {
                label: Some("source_underwater_optics".into()),
                layout: vec![pipelines.effect_layouts[usize::from(multi)].clone()],
                vertex: pipelines.vertex.clone(),
                fragment: Some(FragmentState {
                    shader: pipelines.effect.clone(),
                    shader_defs: vec![ShaderDefVal::Bool("MULTISAMPLED".into(), multi)],
                    targets: vec![Some(ColorTargetState {
                        format,
                        blend: None,
                        write_mask: ColorWrites::ALL,
                    })],
                    ..default()
                }),
                ..default()
            });
            pipelines.effect_pipelines.insert(key, id);
        }
        let mip_layout = cache.get_bind_group_layout(&pipelines.mip_layout);
        let bind_mip = |view: &TextureView| {
            device.create_bind_group(
                "underwater_mip",
                &mip_layout,
                &BindGroupEntries::sequential((view, &pipelines.sampler)),
            )
        };
        let a = target.main_texture_view();
        let b = target.main_texture_other_view();
        let effect = device.create_bind_group(
            "underwater_effect",
            &cache.get_bind_group_layout(&pipelines.effect_layouts[usize::from(multi)]),
            &BindGroupEntries::sequential((&chain.all, &pipelines.sampler, uniform.clone(), depth)),
        );
        commands.entity(entity).insert(Bindings {
            a: (a.id(), bind_mip(a)),
            b: (b.id(), bind_mip(b)),
            mips: chain
                .levels
                .iter()
                .take(chain.levels.len() - 1)
                .map(bind_mip)
                .collect(),
            effect,
            copy_pipeline: pipelines.mip_pipelines[&format].0,
            mip_pipeline: pipelines.mip_pipelines[&format].1,
            effect_pipeline: pipelines.effect_pipelines[&key],
        });
        if let Some(chain) = new {
            commands.entity(entity).insert(chain);
        }
    }
}
fn copy_opaque(
    view: ViewQuery<(
        &ViewTarget,
        &OpticsUniform,
        &DynamicUniformIndex<OpticsUniform>,
        &MipChain,
        &Bindings,
    )>,
    cache: Res<PipelineCache>,
    mut context: RenderContext,
) {
    let (target, settings, _index, chain, bindings) = view.into_inner();
    if settings.state.w == 0. {
        return;
    }
    let Some(copy_pipeline) = cache.get_render_pipeline(bindings.copy_pipeline) else {
        return;
    };
    let Some(mip_pipeline) = cache.get_render_pipeline(bindings.mip_pipeline) else {
        return;
    };
    if cache
        .get_render_pipeline(bindings.effect_pipeline)
        .is_none()
    {
        return;
    }
    let (_, source) = if bindings.a.0 == target.main_texture_view().id() {
        &bindings.a
    } else {
        &bindings.b
    };
    for (i, destination) in chain.levels.iter().enumerate() {
        let bind = if i == 0 {
            source
        } else {
            &bindings.mips[i - 1]
        };
        let mut pass = context
            .command_encoder()
            .begin_render_pass(&RenderPassDescriptor {
                label: Some("underwater_mip_pass"),
                color_attachments: &[Some(RenderPassColorAttachment {
                    view: destination,
                    depth_slice: None,
                    resolve_target: None,
                    ops: Operations::default(),
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
        pass.set_pipeline(if i == 0 { copy_pipeline } else { mip_pipeline });
        pass.set_bind_group(0, bind, &[]);
        pass.draw(0..3, 0..1);
    }
}
fn draw(
    view: ViewQuery<(
        &ViewTarget,
        &OpticsUniform,
        &DynamicUniformIndex<OpticsUniform>,
        &MipChain,
        &Bindings,
    )>,
    cache: Res<PipelineCache>,
    mut context: RenderContext,
) {
    let (target, settings, index, _chain, bindings) = view.into_inner();
    if settings.state.w == 0. {
        return;
    }
    if cache.get_render_pipeline(bindings.copy_pipeline).is_none()
        || cache.get_render_pipeline(bindings.mip_pipeline).is_none()
    {
        return;
    }
    let Some(effect_pipeline) = cache.get_render_pipeline(bindings.effect_pipeline) else {
        return;
    };
    // Flip only after both pipelines and every input are ready; skipping a pass
    // after post_process_write would otherwise replace the world with garbage.
    let output = target.post_process_write();
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_underwater_optics"),
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
    pass.set_pipeline(effect_pipeline);
    pass.set_bind_group(0, &bindings.effect, &[index.index()]);
    pass.draw(0..3, 0..1);
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_scatter_lod_has_a_real_mip_chain_at_all_window_sizes() {
        assert_eq!(mip_count(UVec2::new(1280, 720)), 6);
        assert_eq!(mip_count(UVec2::splat(16)), 5);
        assert_eq!(mip_count(UVec2::ONE), 1);
    }
}
