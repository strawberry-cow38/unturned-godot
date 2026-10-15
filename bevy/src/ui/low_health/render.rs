//! Source pain/low-health copy after the pre-HUD canvas, before sharp HUD UI.
use super::Feedback;
use bevy::{
    core_pipeline::{FullscreenShader, schedule::Core2d},
    prelude::*,
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_component::{ComponentUniforms, DynamicUniformIndex},
        render_resource::{
            binding_types::{sampler, texture_2d, uniform_buffer},
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
            draw.after(bevy::core_pipeline::schedule::Core2dSystems::PostProcess)
                .before(ui_pass),
        );
}
#[derive(Resource)]
struct Pipeline {
    layout: BindGroupLayoutDescriptor,
    sampler: Sampler,
    vertex: VertexState,
    shader: Handle<Shader>,
    formats: HashMap<TextureFormat, CachedRenderPipelineId>,
}
fn initialize(
    mut commands: Commands,
    device: Res<RenderDevice>,
    assets: Res<AssetServer>,
    fullscreen: Res<FullscreenShader>,
) {
    commands.insert_resource(Pipeline {
        layout: BindGroupLayoutDescriptor::new(
            "source_hud_feedback",
            &BindGroupLayoutEntries::sequential(
                ShaderStages::FRAGMENT,
                (
                    texture_2d(TextureSampleType::Float { filterable: true }),
                    sampler(SamplerBindingType::Filtering),
                    uniform_buffer::<Feedback>(true),
                ),
            ),
        ),
        sampler: device.create_sampler(&SamplerDescriptor {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::Linear,
            ..default()
        }),
        vertex: fullscreen.to_vertex_state(),
        shader: assets.load("embedded://unturned_bevy/ui/low_health/overlay.wesl"),
        formats: default(),
    });
}
fn prepare(
    views: Query<(&ViewTarget, &Feedback)>,
    mut pipeline: ResMut<Pipeline>,
    cache: Res<PipelineCache>,
) {
    for (target, settings) in &views {
        // Compile while healthy so the first accepted hurt can draw immediately.
        let _ = settings;
        let format = target.main_texture_format();
        if pipeline.formats.contains_key(&format) {
            continue;
        }
        let id = cache.queue_render_pipeline(RenderPipelineDescriptor {
            label: Some("source_hud_low_health".into()),
            layout: vec![pipeline.layout.clone()],
            vertex: pipeline.vertex.clone(),
            fragment: Some(FragmentState {
                shader: pipeline.shader.clone(),
                targets: vec![Some(ColorTargetState {
                    format,
                    blend: None,
                    write_mask: ColorWrites::ALL,
                })],
                ..default()
            }),
            ..default()
        });
        pipeline.formats.insert(format, id);
    }
}
fn draw(
    view: ViewQuery<(&ViewTarget, &Feedback, &DynamicUniformIndex<Feedback>)>,
    pipeline: Res<Pipeline>,
    cache: Res<PipelineCache>,
    uniforms: Res<ComponentUniforms<Feedback>>,
    mut context: RenderContext,
) {
    let (target, settings, index) = view.into_inner();
    if settings.parameters.x <= 0. && settings.parameters.y <= 0. {
        return;
    }
    let Some(id) = pipeline.formats.get(&target.main_texture_format()) else {
        return;
    };
    let Some(compiled) = cache.get_render_pipeline(*id) else {
        return;
    };
    let Some(uniform) = uniforms.uniforms().binding() else {
        return;
    };
    let output = target.post_process_write();
    let bind = context.render_device().create_bind_group(
        "source_hud_feedback",
        &cache.get_bind_group_layout(&pipeline.layout),
        &BindGroupEntries::sequential((output.source, &pipeline.sampler, uniform)),
    );
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_hud_feedback"),
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
    pass.set_pipeline(compiled);
    pass.set_bind_group(0, &bind, &[index.index()]);
    pass.draw(0..3, 0..1);
}
