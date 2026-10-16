//! ChromaticAberration.cs / content/chromatic.gdshader: source CanvasLayer 7,
//! after the world, hands (5) and night vision (6), before rain (9) and the HUD.
use crate::settings::Settings;
use bevy::{
    asset::embedded_asset,
    core_pipeline::{FullscreenShader, schedule::Core2d},
    prelude::*,
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_component::{
            ComponentUniforms, DynamicUniformIndex, ExtractComponent, ExtractComponentPlugin,
            UniformComponentPlugin,
        },
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

/// Source ChromaticAberration.Samples: six taps along the radial sweep.
pub const SAMPLES: f32 = 6.;
/// chromatic.gdshader `falloff`: r^2 keeps the middle of the frame clean.
pub const FALLOFF: f32 = 2.;

#[derive(Component, Clone, Copy, Debug, PartialEq, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
pub(crate) struct Lens {
    /// intensity, samples, falloff, active
    pub settings: Vec4,
}
impl Lens {
    pub fn from(settings: &Settings) -> Self {
        let g = &settings.graphics;
        // Apply(): OFF is a hidden rect, never a zero-intensity full pass.
        let active = g.chromatic_aberration && g.chromatic_amount > 0.001;
        Self {
            settings: Vec4::new(g.chromatic_amount, SAMPLES, FALLOFF, u8::from(active) as f32),
        }
    }
}

/// Source CanvasLayer 7: after night vision (6), before deadzone grain (8).
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct LensPass;
pub(super) fn install(app: &mut App) {
    embedded_asset!(app, "chromatic.wesl");
    app.add_plugins((
        ExtractComponentPlugin::<Lens>::default(),
        UniformComponentPlugin::<Lens>::default(),
    ))
    .add_systems(PostUpdate, update.in_set(super::GraphicsApply));
    let Some(render) = app.get_sub_app_mut(RenderApp) else {
        return;
    };
    render
        .add_systems(RenderStartup, initialize)
        .add_systems(Render, prepare.in_set(RenderSystems::PrepareBindGroups))
        .add_systems(
            Core2d,
            draw.in_set(LensPass)
                .after(crate::nightvision::render::NightVisionPass)
                .after(crate::hdr::CompositePass)
                .before(ui_pass),
        );
}

pub(super) fn update(
    mut commands: Commands,
    settings: Res<Settings>,
    cameras: Query<(Entity, Option<&Lens>), With<crate::ui::layers::SceneCanvas>>,
) {
    let lens = Lens::from(&settings);
    for (entity, current) in &cameras {
        if current != Some(&lens) {
            commands.entity(entity).insert(lens);
        }
    }
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
            "source_chromatic",
            &BindGroupLayoutEntries::sequential(
                ShaderStages::FRAGMENT,
                (
                    texture_2d(TextureSampleType::Float { filterable: true }),
                    sampler(SamplerBindingType::Filtering),
                    uniform_buffer::<Lens>(true),
                ),
            ),
        ),
        // hint_screen_texture, filter_linear; clamped UVs never sample off-screen.
        sampler: device.create_sampler(&SamplerDescriptor {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::Linear,
            ..default()
        }),
        vertex: fullscreen.to_vertex_state(),
        shader: assets.load("embedded://unturned_bevy/graphics/chromatic.wesl"),
        formats: default(),
    });
}
fn prepare(views: Query<&ViewTarget, With<Lens>>, mut pipeline: ResMut<Pipeline>, cache: Res<PipelineCache>) {
    for target in &views {
        // Prewarm before the row is first switched on.
        let format = target.main_texture_format();
        if pipeline.formats.contains_key(&format) {
            continue;
        }
        let id = cache.queue_render_pipeline(RenderPipelineDescriptor {
            label: Some("source_chromatic_lens".into()),
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
    view: ViewQuery<(&ViewTarget, &Lens, &DynamicUniformIndex<Lens>)>,
    pipeline: Res<Pipeline>,
    cache: Res<PipelineCache>,
    uniforms: Res<ComponentUniforms<Lens>>,
    mut context: RenderContext,
) {
    let (target, lens, index) = view.into_inner();
    if lens.settings.w <= 0. {
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
        "source_chromatic",
        &cache.get_bind_group_layout(&pipeline.layout),
        &BindGroupEntries::sequential((output.source, &pipeline.sampler, uniform)),
    );
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_chromatic"),
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
