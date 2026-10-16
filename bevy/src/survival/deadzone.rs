//! Original DeadzoneOverlay: deadzone.gdshader's grain + desaturation on the
//! scene canvas (source CanvasLayer8, after the night-vision tube and before
//! layer9/HUD10), driven by the accepted owner's radiation dose rather than a
//! second clock. Hidden — no pass at all — while the dose is clear.
use crate::{ui::UiModel, ui::layers::SceneCanvas};
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

/// DeadzoneOverlay.FullExposureDose: the screen is at its worst exactly when the dose is.
pub(crate) const FULL_EXPOSURE_DOSE: f32 = 1.;
/// deadzone.gdshader uniform defaults.
const GRAIN_MAX: f32 = 0.35;
const GRAIN_PX: f32 = 3.;
const DESAT_MAX: f32 = 0.85;

/// DeadzoneOverlay.ExposureFor: 0..1 off the absorbed dose.
pub(crate) fn exposure(dose: f32) -> f32 {
    if dose <= 0. {
        return 0.;
    }
    (dose / FULL_EXPOSURE_DOSE.max(0.01)).clamp(0., 1.)
}

#[derive(Component, Clone, Copy, ExtractComponent, ShaderType, Debug, PartialEq)]
#[extract_app(RenderApp)]
pub(crate) struct GrainUniform {
    /// exposure, time_seconds, grain_px, active
    pub params: Vec4,
    /// grain_max, desat_max
    pub amounts: Vec4,
}
/// UG_DEADZONE=<dose> mirrors DeadzoneOverlay.DebugAttach's harness override.
#[derive(Resource, Default)]
struct Forced(Option<f32>);
pub(crate) struct DeadzonePlugin;
impl Plugin for DeadzonePlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "deadzone.wesl");
        app.add_plugins((
            ExtractComponentPlugin::<GrainUniform>::default(),
            UniformComponentPlugin::<GrainUniform>::default(),
        ))
        .insert_resource(Forced(
            std::env::var("UG_DEADZONE").ok().and_then(|s| s.parse().ok()),
        ))
        .add_systems(Update, update);
        install(app);
    }
}
fn update(
    mut commands: Commands,
    model: Res<UiModel>,
    forced: Res<Forced>,
    time: Res<Time>,
    mut clock: Local<f64>,
    cameras: Query<Entity, With<SceneCanvas>>,
) {
    *clock += time.delta_secs_f64();
    let exposure = exposure(forced.0.unwrap_or(if model.connected { model.radiation } else { 0. }));
    // HIDDEN, not zero-intensity, when clear.
    let active = exposure > 0.001;
    let uniform = GrainUniform {
        params: Vec4::new(exposure, (*clock % 3600.) as f32, GRAIN_PX, u8::from(active) as f32),
        amounts: Vec4::new(GRAIN_MAX, DESAT_MAX, 0., 0.),
    };
    for entity in &cameras {
        commands.entity(entity).insert(uniform);
    }
}

fn install(app: &mut App) {
    let Some(render) = app.get_sub_app_mut(RenderApp) else {
        return;
    };
    render
        .add_systems(RenderStartup, initialize)
        .add_systems(Render, prepare.in_set(RenderSystems::PrepareBindGroups))
        .add_systems(
            Core2d,
            draw.after(crate::nightvision::render::NightVisionPass)
                .after(crate::graphics::chromatic::LensPass)
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
            "source_deadzone",
            &BindGroupLayoutEntries::sequential(
                ShaderStages::FRAGMENT,
                (
                    texture_2d(TextureSampleType::Float { filterable: true }),
                    sampler(SamplerBindingType::Filtering),
                    uniform_buffer::<GrainUniform>(true),
                ),
            ),
        ),
        sampler: device.create_sampler(&SamplerDescriptor {
            mag_filter: FilterMode::Linear,
            min_filter: FilterMode::Linear,
            ..default()
        }),
        vertex: fullscreen.to_vertex_state(),
        shader: assets.load("embedded://unturned_bevy/survival/deadzone.wesl"),
        formats: default(),
    });
}
fn prepare(
    views: Query<&ViewTarget, With<GrainUniform>>,
    mut pipeline: ResMut<Pipeline>,
    cache: Res<PipelineCache>,
) {
    for target in &views {
        let format = target.main_texture_format();
        if pipeline.formats.contains_key(&format) {
            continue;
        }
        let id = cache.queue_render_pipeline(RenderPipelineDescriptor {
            label: Some("source_deadzone_grain".into()),
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
    view: ViewQuery<(&ViewTarget, &GrainUniform, &DynamicUniformIndex<GrainUniform>)>,
    pipeline: Res<Pipeline>,
    cache: Res<PipelineCache>,
    uniforms: Res<ComponentUniforms<GrainUniform>>,
    mut context: RenderContext,
) {
    let (target, settings, index) = view.into_inner();
    if settings.params.w <= 0. {
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
        "source_deadzone",
        &cache.get_bind_group_layout(&pipeline.layout),
        &BindGroupEntries::sequential((output.source, &pipeline.sampler, uniform)),
    );
    let mut pass = context
        .command_encoder()
        .begin_render_pass(&RenderPassDescriptor {
            label: Some("source_deadzone"),
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

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn accepted_dose_drives_only_the_scene_canvas_and_hides_when_clear() {
        assert_eq!(exposure(0.), 0.);
        assert_eq!(exposure(-0.2), 0.);
        assert_eq!(exposure(0.25), 0.25);
        assert_eq!(exposure(3.), 1.);
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .init_resource::<UiModel>()
            .init_resource::<Forced>()
            .add_systems(Update, update);
        let scene = app.world_mut().spawn(SceneCanvas).id();
        let hud = app.world_mut().spawn(Camera2d).id();
        {
            let mut model = app.world_mut().resource_mut::<UiModel>();
            model.connected = true;
            model.radiation = 0.4;
        }
        app.update();
        let grain = *app.world().get::<GrainUniform>(scene).unwrap();
        assert_eq!(grain.params.x, 0.4);
        assert_eq!(grain.params.z, 3.);
        assert_eq!(grain.params.w, 1.);
        assert_eq!(grain.amounts, Vec4::new(0.35, 0.85, 0., 0.));
        assert!(app.world().get::<GrainUniform>(hud).is_none(), "HUD10 stays sharp");
        app.world_mut().resource_mut::<UiModel>().radiation = 0.;
        app.update();
        assert_eq!(app.world().get::<GrainUniform>(scene).unwrap().params.w, 0.);
    }
}
