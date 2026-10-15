//! Godot's finite projection and native infinite projection are compared via an
//! independent full matrix in the CPU reference. GPU helper evaluation is paired
//! with real native shadow rendering; ordinary spotlights are a stable control.
use super::*;
use bevy::{
    app::AppExit,
    asset::embedded_asset,
    camera::{Viewport, visibility::RenderLayers},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    math::{DMat4, DVec3, DVec4},
    render::{
        Render, RenderApp, RenderStartup, RenderSystems,
        extract_resource::{ExtractResource, ExtractResourcePlugin},
        render_resource::*,
        renderer::{RenderDevice, RenderQueue},
        view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    },
    winit::WinitPlugin,
};
use std::sync::{Arc, Mutex};

#[derive(Clone, ShaderType)]
struct Case {
    to_light: Vec4,
    normal: Vec4,
    parameters: Vec4,
    flags: UVec4,
}
fn cases() -> Vec<Case> {
    let mut cases = Vec::new();
    for (position, normal, near, far, texels) in [
        (Vec3::new(0., 0., 8.), Vec3::Z, 0.1, 40., 1024.),
        (Vec3::new(1., 2., 8.), Vec3::Y, 0.1, 40., 1024.),
        (
            Vec3::new(-3., 1., 10.),
            Vec3::new(0.6, 0.8, 0.),
            0.025,
            12.,
            2048.,
        ),
        (
            Vec3::new(4., 2., 25.),
            Vec3::new(0., 0.8, 0.6),
            0.2,
            75.,
            512.,
        ),
    ] {
        for flag in [0, 128] {
            cases.push(Case {
                to_light: position.extend(0.),
                normal: normal.extend(1. / texels),
                parameters: Vec4::new(
                    near,
                    far,
                    0.02,
                    1.8 * 2. * 35_f32.to_radians().tan() / texels * std::f32::consts::SQRT_2,
                ),
                flags: UVec4::new(flag, 0, 0, 0),
            });
        }
    }
    cases
}
// Literal source OpenGL perspective, reverse-Z correction and light-bias matrix.
// This deliberately does not restate the simplified production depth equation.
fn godot_clip(point: Vec3, near: f32, far: f32) -> DVec4 {
    let near = near as f64;
    let far = far as f64;
    let projection = DMat4::from_cols(
        DVec4::X,
        DVec4::Y,
        DVec4::new(0., 0., -(far + near) / (far - near), -1.),
        DVec4::new(0., 0., -2. * near * far / (far - near), 0.),
    );
    let reverse = DMat4::from_diagonal(DVec4::new(1., 1., -1., 1.));
    let bias = DMat4::from_cols(
        DVec4::new(0.5, 0., 0., 0.),
        DVec4::new(0., 0.5, 0., 0.),
        DVec4::new(0., 0., 0.5, 0.),
        DVec4::new(0.5, 0.5, 0.5, 1.),
    );
    bias * reverse * projection * DVec3::from(point).extend(1.)
}
fn expected(case: &Case) -> Vec4 {
    let toward = case.to_light.truncate();
    let normal = case.normal.truncate();
    if case.flags.x == 0 {
        let receiver = -toward
            + case.parameters.z * toward.normalize()
            + normal * case.parameters.w * toward.z;
        return receiver.extend(case.parameters.x / -receiver.z);
    }
    let receiver = -toward
        + normal
            * toward.length()
            * 10.
            * case.normal.w
            * (1. - normal.dot(toward.normalize()).abs());
    let source_near = 0.025_f32.min(case.parameters.y);
    let mut clip = godot_clip(receiver, source_near, case.parameters.y);
    clip.z += 0.03 / 100. * 2.; // Light3D ShadowBlur1 × default SOFT_LOW PCF radius2.
    let source_depth = clip.z / clip.w;
    // An affine remap between the two actual depth projections preserves the
    // receiver/occluder comparison, including unequal near planes and finite far.
    let far = case.parameters.y as f64;
    let source_near = source_near as f64;
    let native_depth = (source_depth + source_near / (far - source_near))
        * case.parameters.x as f64
        / (source_near * far / (far - source_near));
    receiver.extend(native_depth as f32)
}
#[test]
fn homogeneous_bias_reference_preserves_occlusion_at_multiple_near_and_far_planes() {
    for case in cases().into_iter().filter(|c| c.flags.x != 0) {
        let receiver = expected(&case);
        let near = 0.025_f32.min(case.parameters.y);
        let mut clip = godot_clip(receiver.truncate(), near, case.parameters.y);
        clip.z += 0.0006;
        for distance in [0.5, 5., 7., 8., 8.1, 10., 20., 30., 60.] {
            let blocker = godot_clip(Vec3::new(0., 0., -distance), near, case.parameters.y);
            assert_eq!(
                clip.z / clip.w >= blocker.z / blocker.w,
                receiver.w >= case.parameters.x / distance
            );
        }
    }
    let direct = &cases()[1];
    let actual = expected(direct).w;
    assert!((actual - (0.1 + 0.0006 * 0.1 * (40. - 0.025) / (0.025 * 40.)) / 8.).abs() < 1e-7);
    assert!(
        (actual - (0.1 + 0.0006) / 8.).abs() > 1e-4,
        "merely adding the source homogeneous offset to a different projection is wrong"
    );
}

#[derive(Resource, Clone, Default, ExtractResource)]
#[extract_app(RenderApp)]
struct Readback(Arc<Mutex<Option<Vec<Vec4>>>>);
#[derive(Resource)]
struct Probe {
    layout: BindGroupLayoutDescriptor,
    pipeline: CachedComputePipelineId,
    cases: StorageBuffer<Vec<Case>>,
    output: Buffer,
    readback: Buffer,
}
#[derive(Component)]
struct Headlight;
#[derive(Resource)]
struct Capture {
    path: String,
    stage: u32,
    age: u32,
    requested: bool,
    done: Arc<Mutex<bool>>,
}

#[test]
#[ignore = "requires Vulkan/display; UG_SHADOW_BIAS_SHOT sets PNG prefix"]
fn gpu_shadow_bias_matches_source_projection_and_leaves_unmarked_lights_unchanged() {
    let path = std::env::var("UG_SHADOW_BIAS_SHOT")
        .unwrap_or_else(|_| "/tmp/bevy-headlight-shadow".into());
    let readback = Readback::default();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1024, 512).into(),
                    title: "Marked source headlight / unchanged native spotlight".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins(ExtractResourcePlugin::<Readback>::default())
    .insert_resource(readback.clone())
    .insert_resource(GlobalAmbientLight {
        brightness: 0.,
        ..default()
    })
    .insert_resource(ClearColor(Color::BLACK))
    .insert_resource(Capture {
        path: path.clone(),
        stage: 0,
        age: 0,
        requested: false,
        done: default(),
    })
    .add_systems(Startup, (crate::hdr::layers::configure, setup))
    .add_systems(Update, capture);
    super::install(&mut app);
    embedded_asset!(app, "shadow_tests/probe.wesl");
    app.sub_app_mut(RenderApp)
        .add_systems(RenderStartup, initialize)
        .add_systems(Render, evaluate.in_set(RenderSystems::PrepareResources));
    app.run();
    let values = readback.0.lock().unwrap();
    let actual = values.as_ref().expect("actual GPU helper results");
    assert_eq!(actual.len(), cases().len());
    for (index, (case, gpu)) in cases().iter().zip(actual).enumerate() {
        let want = expected(case);
        assert!(
            gpu.abs_diff_eq(want, 2e-6),
            "case{index}: GPU {gpu:?}, independent source/native reference{want:?}"
        );
    }
    let images: Vec<_> = (0..3)
        .map(|stage| {
            Image::from_buffer(
                &std::fs::read(format!("{path}-{stage}.png")).unwrap(),
                bevy::image::ImageType::Extension("png"),
                bevy::image::CompressedImageFormats::NONE,
                true,
                bevy::image::ImageSampler::nearest(),
                bevy::asset::RenderAssetUsages::default(),
            )
            .unwrap()
            .try_into_dynamic()
            .unwrap()
            .into_rgb8()
        })
        .collect();
    let difference = |a: usize, b: usize, view: u32| {
        let mut sum = 0_u64;
        let mut changed = 0;
        for y in 0..512 {
            for x in view * 512..(view + 1) * 512 {
                let delta: u64 = images[a]
                    .get_pixel(x, y)
                    .0
                    .iter()
                    .zip(images[b].get_pixel(x, y).0)
                    .map(|(a, b)| a.abs_diff(b) as u64)
                    .sum();
                sum += delta;
                if delta > 3 {
                    changed += 1;
                }
            }
        }
        (sum, changed)
    };
    let changed = difference(0, 1, 0);
    assert!(
        changed.1 > 20,
        "source bias must reach real rendered receiver samples: {changed:?}"
    );
    assert_eq!(
        difference(0, 1, 1),
        (0, 0),
        "ordinary spotlight must remain pixel-identical"
    );
    assert_eq!(
        difference(0, 2, 0),
        (0, 0),
        "restoring marked profile must reproduce original shadow pixels"
    );
    std::fs::write(format!("{path}.json"),serde_json::to_vec_pretty(&serde_json::json!({"sourceChangedPixels":changed.1,"unmarkedChangedPixels":0,"samples":actual.iter().map(|v|v.to_array()).collect::<Vec<_>>()})).unwrap()).unwrap();
}
fn setup(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let material = materials.add(StandardMaterial {
        base_color: Color::srgb(0.7, 0.7, 0.7),
        perceptual_roughness: 1.,
        ..default()
    });
    for view in 0..2 {
        let layer = RenderLayers::layer(view);
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::new(14., 0.2, 20.))),
            MeshMaterial3d(material.clone()),
            Transform::from_xyz(0., -0.1, -4.),
            layer.clone(),
        ));
        for (pos, size) in [
            (Vec3::new(0., 0.6, -1.), Vec3::new(1.3, 1.2, 0.7)),
            (Vec3::new(-1.8, 0.25, -5.), Vec3::new(1., 0.5, 1.)),
            (Vec3::new(1.5, 0.9, -7.), Vec3::new(0.5, 1.8, 0.5)),
        ] {
            commands.spawn((
                Mesh3d(meshes.add(Cuboid::from_size(size))),
                MeshMaterial3d(material.clone()),
                Transform::from_translation(pos),
                layer.clone(),
            ));
        }
        let mut light = commands.spawn((
            SpotLight {
                intensity: crate::hdr::point_lumens(3.),
                range: 30.,
                outer_angle: 25_f32.to_radians(),
                inner_angle: 10_f32.to_radians(),
                shadow_maps_enabled: true,
                ..default()
            },
            Transform::from_xyz(0., 1.2, 4.).looking_at(Vec3::new(0., 0., -6.), Vec3::Y),
            layer.clone(),
        ));
        if view == 0 {
            light.insert((Headlight, SourceFalloff::new(1., 1.).with_headlight_fade()));
        }
        commands.spawn((
            Camera3d::default(),
            Camera {
                order: view as isize,
                viewport: Some(Viewport {
                    physical_position: UVec2::new(view as u32 * 512, 0),
                    physical_size: UVec2::splat(512),
                    ..default()
                }),
                ..default()
            },
            Projection::Orthographic(OrthographicProjection {
                scaling_mode: bevy::camera::ScalingMode::FixedVertical {
                    viewport_height: 17.,
                },
                ..OrthographicProjection::default_3d()
            }),
            Tonemapping::None,
            DebandDither::Disabled,
            Msaa::Off,
            Transform::from_xyz(7., 9., 9.).looking_at(Vec3::new(0., 0., -4.), Vec3::Y),
            layer,
        ));
    }
}
fn capture(
    mut commands: Commands,
    mut capture: ResMut<Capture>,
    readback: Res<Readback>,
    lights: Query<Entity, With<Headlight>>,
    mut exit: MessageWriter<AppExit>,
) {
    capture.age += 1;
    assert!(capture.age < 1000, "shadow fixture timed out");
    if capture.age > 45 && !capture.requested && readback.0.lock().unwrap().is_some() {
        let done = capture.done.clone();
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!(
                "{}-{}.png",
                capture.path, capture.stage
            )))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        capture.requested = true;
    }
    if !*capture.done.lock().unwrap() {
        return;
    }
    if capture.stage == 2 {
        exit.write(AppExit::Success);
        return;
    }
    capture.stage += 1;
    for entity in &lights {
        let profile = SourceFalloff::new(1., 1.);
        commands.entity(entity).insert(if capture.stage == 1 {
            profile
        } else {
            profile.with_headlight_fade()
        });
    }
    capture.age = 0;
    capture.requested = false;
    capture.done = default();
}
fn initialize(
    mut commands: Commands,
    assets: Res<AssetServer>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
    queue: Res<RenderQueue>,
) {
    let entry = |binding, read_only| BindGroupLayoutEntry {
        binding,
        visibility: ShaderStages::COMPUTE,
        ty: BindingType::Buffer {
            ty: BufferBindingType::Storage { read_only },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    };
    let layout =
        BindGroupLayoutDescriptor::new("headlight bias probe", &[entry(0, true), entry(1, false)]);
    let pipeline = cache.queue_compute_pipeline(ComputePipelineDescriptor {
        label: Some("headlight bias probe".into()),
        layout: vec![layout.clone()],
        shader: assets.load("embedded://unturned_bevy/hdr/light_units/shadow_tests/probe.wesl"),
        entry_point: Some("evaluate".into()),
        ..default()
    });
    let mut inputs = StorageBuffer::from(cases());
    inputs.write_buffer(&device, &queue);
    let size = cases().len() as u64 * 16;
    let output = device.create_buffer(&BufferDescriptor {
        label: Some("headlight bias output"),
        size,
        usage: BufferUsages::STORAGE | BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let readback = device.create_buffer(&BufferDescriptor {
        label: Some("headlight bias readback"),
        size,
        usage: BufferUsages::MAP_READ | BufferUsages::COPY_DST,
        mapped_at_creation: false,
    });
    commands.insert_resource(Probe {
        layout,
        pipeline,
        cases: inputs,
        output,
        readback,
    });
}
fn evaluate(
    probe: Res<Probe>,
    readback: Res<Readback>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
    queue: Res<RenderQueue>,
) {
    if readback.0.lock().unwrap().is_some() {
        return;
    }
    let Some(pipeline) = cache.get_compute_pipeline(probe.pipeline) else {
        return;
    };
    let group = device.create_bind_group(
        "headlight bias probe",
        &cache.get_bind_group_layout(&probe.layout),
        &BindGroupEntries::sequential((
            probe.cases.binding().unwrap(),
            probe.output.as_entire_binding(),
        )),
    );
    let mut encoder = device.create_command_encoder(&default());
    {
        let mut pass = encoder.begin_compute_pass(&default());
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, &group, &[]);
        pass.dispatch_workgroups(cases().len() as u32, 1, 1);
    }
    encoder.copy_buffer_to_buffer(
        &probe.output,
        0,
        &probe.readback,
        0,
        cases().len() as u64 * 16,
    );
    queue.submit([encoder.finish()]);
    let (tx, rx) = std::sync::mpsc::channel();
    probe.readback.slice(..).map_async(MapMode::Read, move |r| {
        tx.send(r).unwrap();
    });
    device
        .poll(PollType::Wait {
            submission_index: None,
            timeout: None,
        })
        .unwrap();
    rx.recv().unwrap().unwrap();
    let mapped = probe.readback.slice(..).get_mapped_range().unwrap();
    let values = mapped
        .chunks_exact(16)
        .map(|r| {
            Vec4::from_array(std::array::from_fn(|i| {
                f32::from_le_bytes(r[i * 4..i * 4 + 4].try_into().unwrap())
            }))
        })
        .collect();
    drop(mapped);
    probe.readback.unmap();
    *readback.0.lock().unwrap() = Some(values);
}
