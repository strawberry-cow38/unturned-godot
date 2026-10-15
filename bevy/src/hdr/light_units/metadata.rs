//! Source local-light exponents in the pinned Bevy clustered-light record.
//! Bevy0.20 reserves bits0..6 for its flags. A compute pass preserves bits0..6, uses bit7 for source headlight fading,
//! and fills the remaining24bits with two twelve-bit decimal exponents.
//! It uses Bevy's own shader struct, leaving shadow data, clustering, textures,
//! and all material/view bindings intact. No CPU layout offsets are duplicated.
use bevy::{
    asset::embedded_asset,
    pbr::{ExtractedPointLight, GlobalClusterableObjectMeta},
    prelude::*,
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_component::{ExtractComponent, ExtractComponentPlugin},
        render_resource::*,
        renderer::{RenderDevice, RenderQueue},
    },
};

/// Authored Godot distance and cone exponents. An absent component retains the
/// original default1. Known source values are exact decimal thousandths.
#[derive(Component, Clone, Copy, Debug, PartialEq, Eq, ExtractComponent)]
#[extract_app(RenderApp)]
pub(crate) struct SourceFalloff(u32);
impl SourceFalloff {
    pub(crate) fn new(distance: f32, cone: f32) -> Self {
        fn encode(value: f32) -> u32 {
            assert!(
                value.is_finite() && value >= 0.001 && value <= 4.095,
                "source light exponent requires an explicit wider renderer encoding: {value}"
            );
            let code = (value * 1000.).round() as u32;
            assert!(
                (code as f32 / 1000. - value).abs() < 0.00001,
                "source light exponent must retain its authored precision: {value}"
            );
            if code == 1000 { 0 } else { code }
        }
        Self((encode(distance) << 8) | (encode(cone) << 20))
    }

    /// Original headlights: energy fades60→85m, shadow opacity50→75m.
    /// Evaluation is per view, so mirrors and scope cameras keep their own fade.
    pub(crate) fn with_headlight_fade(mut self) -> Self {
        self.0 |= 128;
        self
    }
}

pub(super) fn install(app: &mut App) {
    embedded_asset!(app, "metadata.wesl");
    app.add_plugins(ExtractComponentPlugin::<SourceFalloff>::default());
    let Some(render) = app.get_sub_app_mut(RenderApp) else {
        return;
    };
    render.add_systems(RenderStartup, initialize).add_systems(
        Render,
        patch
            .in_set(RenderSystems::PrepareResources)
            .after(bevy::pbr::prepare_lights),
    );
}

#[derive(Resource)]
struct Pipeline {
    layout: BindGroupLayoutDescriptor,
    pipeline: CachedComputePipelineId,
    profiles: StorageBuffer<ProfileRows>,
}
// StorageBuffer's serialization scratch retains capacity when a runtime array
// shrinks. arrayLength() can therefore include old rows: an explicit logical
// count prevents removed profiles from targeting newly reordered lights.
#[derive(Default, ShaderType)]
struct ProfileRows {
    count: u32,
    #[shader(size(runtime))]
    entries: Vec<UVec2>,
}
fn initialize(mut commands: Commands, assets: Res<AssetServer>, cache: Res<PipelineCache>) {
    let buffer = |read_only| BindGroupLayoutEntry {
        binding: 0,
        visibility: ShaderStages::COMPUTE,
        ty: BindingType::Buffer {
            ty: BufferBindingType::Storage { read_only },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    };
    let layout = BindGroupLayoutDescriptor::new(
        "source_light_profiles",
        &[
            buffer(false),
            BindGroupLayoutEntry {
                binding: 1,
                ..buffer(true)
            },
        ],
    );
    let pipeline = cache.queue_compute_pipeline(ComputePipelineDescriptor {
        label: Some("source_light_profiles".into()),
        layout: vec![layout.clone()],
        shader: assets.load("embedded://unturned_bevy/hdr/light_units/metadata.wesl"),
        entry_point: Some("apply_profiles".into()),
        ..default()
    });
    commands.insert_resource(Pipeline {
        layout,
        pipeline,
        profiles: default(),
    });
}

fn patch(
    profiles: Query<(Entity, &SourceFalloff), With<ExtractedPointLight>>,
    lights: Res<GlobalClusterableObjectMeta>,
    mut state: ResMut<Pipeline>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
    queue: Res<RenderQueue>,
) {
    let Some(pipeline) = cache.get_compute_pipeline(state.pipeline) else {
        return;
    };
    let rows: Vec<_> = profiles
        .iter()
        .filter_map(|(entity, profile)| {
            let index = *lights.entity_to_index.get(&entity)?;
            (profile.0 != 0).then_some(UVec2::new(index as u32, profile.0))
        })
        .collect();
    if rows.is_empty() {
        return;
    }
    let Some(binding) = lights.gpu_clustered_lights.binding() else {
        return;
    };
    let BindingResource::Buffer(ref buffer) = binding else {
        unreachable!()
    };
    assert!(
        buffer.buffer.usage().contains(BufferUsages::STORAGE),
        "authored source light profiles require native clustered storage buffers"
    );
    let count = rows.len() as u32;
    state.profiles.set(ProfileRows {
        count,
        entries: rows,
    });
    state.profiles.write_buffer(&device, &queue);
    let group = device.create_bind_group(
        "source_light_profiles",
        &cache.get_bind_group_layout(&state.layout),
        &BindGroupEntries::sequential((binding, state.profiles.binding().unwrap())),
    );
    let mut encoder = device.create_command_encoder(&CommandEncoderDescriptor {
        label: Some("source_light_profiles"),
    });
    {
        let mut pass = encoder.begin_compute_pass(&ComputePassDescriptor {
            label: Some("source_light_profiles"),
            timestamp_writes: None,
        });
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, &group, &[]);
        pass.dispatch_workgroups(count.div_ceil(64), 1, 1);
    }
    // prepare_lights has queued the original records. Submission flushes those
    // writes before this pass; later view rendering consumes the patched buffer.
    queue.submit([encoder.finish()]);
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[should_panic(expected = "wider renderer encoding")]
    fn tiny_positive_exponent_cannot_alias_default_sentinel() {
        SourceFalloff::new(0.000001, 1.);
    }

    #[test]
    fn original_lamp_profiles_preserve_all_native_flag_bits_and_exact_constants() {
        let decode = |code: u32| if code == 0 { 1. } else { code as f32 / 1000. };
        for (distance, cone) in [(1., 1.), (1., 1.3), (1., 0.85), (1., 0.6), (1.5, 1.)] {
            let encoded = SourceFalloff::new(distance, cone).0;
            for flags in 0..128 {
                let patched = (flags & 127) | encoded;
                assert_eq!(patched & 127, flags);
                assert_eq!(decode((patched >> 8) & 4095), distance);
                assert_eq!(decode((patched >> 20) & 4095), cone);
            }
        }
        assert_eq!(SourceFalloff::new(1., 1.).0, 0);
        assert_eq!(SourceFalloff::new(1., 1.).with_headlight_fade().0, 128);
    }
}

#[cfg(test)]
mod visual;
