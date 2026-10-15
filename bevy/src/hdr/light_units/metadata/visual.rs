//! Uses the production clustered buffer and adapted shader functions, not a
//! CPU mirror of either layout. Readback is intentionally synchronous only in
//! this opt-in fixture so before/after records belong to the same render frame.
use super::*;
use bevy::{
    app::AppExit,
    camera::Viewport,
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    render::{
        extract_resource::{ExtractResource, ExtractResourcePlugin},
        view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    },
    winit::WinitPlugin,
};
use std::sync::{Arc, Mutex};

#[derive(Resource, Clone, Default, ExtractResource)]
#[extract_app(RenderApp)]
struct Stage(u32);
#[derive(Resource, Clone, Default, ExtractResource)]
#[extract_app(RenderApp)]
struct Readbacks(Arc<Mutex<Vec<Frame>>>);
#[derive(Debug, Clone)]
struct Frame {
    stage: u32,
    rows: Vec<Row>,
}
#[derive(Debug, Clone)]
struct Row {
    index: usize,
    expected: u32,
    words: Vec<u32>,
}
#[derive(Resource)]
struct Probe {
    layout: BindGroupLayoutDescriptor,
    before: CachedComputePipelineId,
    after: CachedComputePipelineId,
    output: Buffer,
    readback: Buffer,
}
#[derive(Component)]
struct Lamp(u8);
#[derive(Resource)]
struct Capture {
    path: String,
    requested: bool,
    finished: Arc<Mutex<bool>>,
    age: u32,
}

#[test]
#[ignore = "requires Vulkan/display; UG_LIGHT_PROFILE_SHOT sets PNG prefix"]
fn gpu_profiles_preserve_native_records_across_changes_removal_and_reordering() {
    let path = std::env::var("UG_LIGHT_PROFILE_SHOT")
        .unwrap_or_else(|_| "/tmp/bevy-light-profiles".into());
    let results = Readbacks::default();
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    resolution: (1152, 384).into(),
                    title: "Source profiles: near / middle / beyond headlight fade".into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .add_plugins((
        ExtractResourcePlugin::<Stage>::default(),
        ExtractResourcePlugin::<Readbacks>::default(),
    ))
    .init_resource::<Stage>()
    .insert_resource(results.clone())
    .insert_resource(Capture {
        path: path.clone(),
        requested: false,
        finished: default(),
        age: 0,
    })
    .insert_resource(GlobalAmbientLight {
        brightness: 0.,
        ..default()
    })
    .insert_resource(ClearColor(Color::BLACK))
    .add_systems(Startup, setup)
    .add_systems(Update, advance);
    super::super::install(&mut app);
    embedded_asset!(app, "probe.wesl");
    app.sub_app_mut(RenderApp)
        .add_systems(RenderStartup, initialize_probe)
        .add_systems(
            Render,
            (
                before
                    .in_set(RenderSystems::PrepareResources)
                    .after(bevy::pbr::prepare_lights)
                    .before(super::patch),
                after
                    .in_set(RenderSystems::PrepareResources)
                    .after(super::patch)
                    .after(before),
            ),
        );
    app.run();
    // Compare simultaneous views of the same scene, never a stored golden.
    // The orthographic cameras have identical framing/direction at different
    // distances, so only flagged source attenuation changes this ground patch.
    let means = |stage| {
        let image = Image::from_buffer(
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
        .into_rgb8();
        assert_eq!(image.dimensions(), (1152, 384));
        (0..3)
            .map(|view| {
                let mut sum = 0_u64;
                for y in 205..240 {
                    for x in 90..155 {
                        sum += image
                            .get_pixel(view * 384 + x, y)
                            .0
                            .iter()
                            .map(|v| *v as u64)
                            .sum::<u64>();
                    }
                }
                sum as f32 / (3. * 35. * 65.)
            })
            .collect::<Vec<_>>()
    };
    for stage in 0..2 {
        let value = means(stage);
        assert!(
            value[0] > value[1] && value[1] > value[2],
            "headlight fade must be evaluated per view: {value:?}"
        );
        assert!(
            value[0] > value[2] * 1.2,
            "actual PBR lighting must consume source fade, not merely carry metadata: {value:?}"
        );
    }
    let plain = means(2);
    assert!(
        (plain[0] - plain[1]).abs() < 0.1 && (plain[1] - plain[2]).abs() < 0.1,
        "removing the last profile must restore the unflagged view result: {plain:?}"
    );
    let frames = results.0.lock().unwrap();
    for stage in 0..3 {
        let frame = frames
            .iter()
            .rev()
            .find(|f| f.stage == stage)
            .expect("every stage must return actual GPU records");
        validate(frame);
    }
    let index = |stage: u32, x: f32| {
        frames
            .iter()
            .rev()
            .find(|f| f.stage == stage)
            .unwrap()
            .rows
            .iter()
            .find(|r| f32::from_bits(r.words[4]) == x)
            .unwrap()
            .index
    };
    assert_ne!(
        index(0, -2.),
        index(1, -2.),
        "spot/point replacement must reorder a surviving light's GPU index"
    );
    let report:Vec<_>=frames.iter().filter(|f| frames.iter().rev().find(|other|other.stage==f.stage).is_some_and(|last|std::ptr::eq(last,*f))).map(|f|serde_json::json!({"stage":f.stage,"lights":f.rows.iter().map(|r|serde_json::json!({"index":r.index,"expectedFlags":r.expected,"beforeFlags":r.words[1],"afterFlags":r.words[33],"position":r.words[4..7].iter().map(|w|f32::from_bits(*w)).collect::<Vec<_>>(),"distanceSamples":r.words[40..44].iter().map(|w|f32::from_bits(*w)).collect::<Vec<_>>(),"coneSamples":r.words[44..48].iter().map(|w|f32::from_bits(*w)).collect::<Vec<_>>(),"energyFade":r.words[48..52].iter().map(|w|f32::from_bits(*w)).collect::<Vec<_>>(),"shadowFade":r.words[52..56].iter().map(|w|f32::from_bits(*w)).collect::<Vec<_>>() })).collect::<Vec<_>>()})).collect();
    std::fs::write(
        format!("{path}.json"),
        serde_json::to_vec_pretty(&report).unwrap(),
    )
    .unwrap();
}

fn validate(frame: &Frame) {
    assert_eq!(frame.rows.len(), if frame.stage == 0 { 4 } else { 3 });
    for row in &frame.rows {
        let before = &row.words[..32];
        let x = f32::from_bits(before[4]);
        assert_eq!(before[1] & 1 != 0, x != 2., "source test shadow maps flag");
        assert_eq!(
            before[1] & 32 != 0,
            x == -2. || x == 4.,
            "mixed point/spot classification"
        );
        let after = &row.words[32..];
        assert_eq!(
            before[1] & !127,
            0,
            "prepare_lights must reset reserved flags every frame"
        );
        assert_eq!(
            after[1],
            (before[1] & 127) | row.expected,
            "stage{} index{} metadata must follow actual entity-to-index map",
            frame.stage,
            row.index
        );
        assert_eq!(&before[4..8], &after[4..8], "position/radius untouched");
        assert_eq!(
            &before[24..32],
            &after[24..32],
            "native shadow/bias/color/range untouched"
        );
        let decode = |n: u32| if n == 0 { 1. } else { n as f32 / 1000. };
        let decay = decode((row.expected >> 8) & 4095);
        let cone = decode((row.expected >> 20) & 4095);
        let inv = f32::from_bits(after[31]);
        for (j, d) in [2_f32, 5., 0.0001, 10.].into_iter().enumerate() {
            let want = (1. - (d * d * inv).powi(2)).max(0.).powi(2) * d.powf(-decay);
            let got = f32::from_bits(after[8 + j]);
            assert!(
                (got - want).abs() <= 1e-5 * want.max(1.),
                "distance sample {got} != {want}"
            );
        }
        for (j, rim) in [0.0001_f32, 0.5, 1., 1.].into_iter().enumerate() {
            let want = 1. - rim.powf(cone);
            let got = f32::from_bits(after[12 + j]);
            assert!((got - want).abs() < 2e-6, "cone {got} != {want}");
        }
        for base in [16, 20] {
            for (j, fade) in [1., 0.84375, 0.5, 0.].into_iter().enumerate() {
                let want = if row.expected & 128 != 0 { fade } else { 1. };
                assert!((f32::from_bits(after[base + j]) - want).abs() < 1e-6);
            }
        }
    }
}

fn setup(
    mut commands: Commands,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let white = materials.add(StandardMaterial {
        base_color: Color::srgb(0.65, 0.65, 0.65),
        perceptual_roughness: 1.,
        ..default()
    });
    commands.spawn((
        Mesh3d(meshes.add(Cuboid::new(13., 0.2, 12.))),
        MeshMaterial3d(white.clone()),
        Transform::from_xyz(0., -0.1, 0.),
    ));
    for x in [-3., 0., 3.] {
        commands.spawn((
            Mesh3d(meshes.add(Cuboid::new(0.8, 1.5, 0.8))),
            MeshMaterial3d(white.clone()),
            Transform::from_xyz(x, 0.75, 0.),
        ));
    }
    commands.spawn((
        Lamp(0),
        PointLight {
            intensity: crate::hdr::point_lumens(0.7),
            range: 10.,
            shadow_maps_enabled: true,
            ..default()
        },
        SourceFalloff::new(1.5, 1.),
        Transform::from_xyz(-4., 3., 0.),
    ));
    commands.spawn((
        Lamp(1),
        SpotLight {
            intensity: crate::hdr::point_lumens(1.5),
            range: 10.,
            outer_angle: 35_f32.to_radians(),
            inner_angle: 10_f32.to_radians(),
            shadow_maps_enabled: true,
            ..default()
        },
        SourceFalloff::new(1., 0.6).with_headlight_fade(),
        Transform::from_xyz(-2., 4., 3.).looking_at(Vec3::new(-2., 0., 0.), Vec3::Y),
    ));
    commands.spawn((
        Lamp(2),
        PointLight {
            intensity: crate::hdr::point_lumens(0.3),
            range: 10.,
            ..default()
        },
        SourceFalloff::new(1.3, 1.),
        Transform::from_xyz(2., 3., 0.),
    ));
    commands.spawn((
        Lamp(3),
        SpotLight {
            intensity: crate::hdr::point_lumens(1.),
            range: 10.,
            outer_angle: 35_f32.to_radians(),
            shadow_maps_enabled: true,
            ..default()
        },
        SourceFalloff::new(1., 1.3),
        Transform::from_xyz(4., 4., 3.).looking_at(Vec3::new(4., 0., 0.), Vec3::Y),
    ));
    for (i, d) in [12., 72.5, 90.].into_iter().enumerate() {
        commands.spawn((
            Camera3d::default(),
            Camera {
                order: i as isize,
                viewport: Some(Viewport {
                    physical_position: UVec2::new(i as u32 * 384, 0),
                    physical_size: UVec2::splat(384),
                    ..default()
                }),
                ..default()
            },
            Projection::Orthographic(OrthographicProjection {
                scaling_mode: bevy::camera::ScalingMode::FixedVertical {
                    viewport_height: 13.,
                },
                ..OrthographicProjection::default_3d()
            }),
            Tonemapping::None,
            DebandDither::Disabled,
            Msaa::Off,
            Transform::from_translation(Vec3::new(0., 0.65, 1.).normalize() * d)
                .looking_at(Vec3::ZERO, Vec3::Y),
        ));
    }
}

fn advance(
    mut commands: Commands,
    mut stage: ResMut<Stage>,
    mut capture: ResMut<Capture>,
    results: Res<Readbacks>,
    lamps: Query<(Entity, &Lamp)>,
    mut exit: MessageWriter<AppExit>,
) {
    capture.age += 1;
    assert!(capture.age < 1000, "GPU profile test timed out");
    if !capture.requested
        && capture.age > 30
        && results.0.lock().unwrap().iter().any(|f| f.stage == stage.0)
    {
        let done = capture.finished.clone();
        commands
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!("{}-{}.png", capture.path, stage.0)))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        capture.requested = true;
    }
    if !*capture.finished.lock().unwrap() {
        return;
    }
    if stage.0 == 2 {
        exit.write(AppExit::Success);
        return;
    }
    if stage.0 == 0 {
        for (entity, lamp) in &lamps {
            match lamp.0 {
                0 => {
                    commands.entity(entity).despawn();
                }
                1 => {
                    commands
                        .entity(entity)
                        .insert(SourceFalloff::new(1.5, 0.85).with_headlight_fade());
                }
                2 => {
                    commands.entity(entity).remove::<SourceFalloff>();
                }
                3 => {
                    commands.entity(entity).insert(SourceFalloff::new(1., 1.));
                }
                _ => {}
            }
        }
    } else {
        for (entity, _) in &lamps {
            commands.entity(entity).remove::<SourceFalloff>();
        }
    }
    stage.0 += 1;
    capture.requested = false;
    capture.finished = default();
    capture.age = 0;
}

fn initialize_probe(
    mut commands: Commands,
    assets: Res<AssetServer>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
) {
    let storage = |binding, read_only| BindGroupLayoutEntry {
        binding,
        visibility: ShaderStages::COMPUTE,
        ty: BindingType::Buffer {
            ty: BufferBindingType::Storage { read_only },
            has_dynamic_offset: false,
            min_binding_size: None,
        },
        count: None,
    };
    let layout = BindGroupLayoutDescriptor::new(
        "light profile fixture",
        &[storage(0, true), storage(1, false)],
    );
    let make = |entry: &'static str| {
        cache.queue_compute_pipeline(ComputePipelineDescriptor {
            label: Some(format!("light profile {entry}").into()),
            layout: vec![layout.clone()],
            shader: assets.load("embedded://unturned_bevy/hdr/light_units/metadata/probe.wesl"),
            entry_point: Some(entry.into()),
            ..default()
        })
    };
    let output = device.create_buffer(&BufferDescriptor {
        label: Some("light profile output"),
        size: 4096,
        usage: BufferUsages::STORAGE | BufferUsages::COPY_SRC,
        mapped_at_creation: false,
    });
    let readback = device.create_buffer(&BufferDescriptor {
        label: Some("light profile readback"),
        size: 4096,
        usage: BufferUsages::COPY_DST | BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });
    commands.insert_resource(Probe {
        before: make("before"),
        after: make("after"),
        layout,
        output,
        readback,
    });
}
fn before(
    probe: Res<Probe>,
    production: Res<super::Pipeline>,
    lights: Res<GlobalClusterableObjectMeta>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
    queue: Res<RenderQueue>,
) {
    if cache.get_compute_pipeline(production.pipeline).is_none() {
        return;
    }
    dispatch(&probe, &lights, &cache, &device, &queue, false);
}
fn dispatch(
    probe: &Probe,
    lights: &GlobalClusterableObjectMeta,
    cache: &PipelineCache,
    device: &RenderDevice,
    queue: &RenderQueue,
    after: bool,
) -> bool {
    let Some(pipeline) = cache.get_compute_pipeline(if after { probe.after } else { probe.before })
    else {
        return false;
    };
    let Some(binding) = lights.gpu_clustered_lights.binding() else {
        return false;
    };
    let group = device.create_bind_group(
        "light profile fixture",
        &cache.get_bind_group_layout(&probe.layout),
        &BindGroupEntries::sequential((binding, probe.output.as_entire_binding())),
    );
    let mut encoder = device.create_command_encoder(&default());
    {
        let mut pass = encoder.begin_compute_pass(&default());
        pass.set_pipeline(pipeline);
        pass.set_bind_group(0, &group, &[]);
        pass.dispatch_workgroups(1, 1, 1);
    }
    if after {
        encoder.copy_buffer_to_buffer(&probe.output, 0, &probe.readback, 0, 4096);
    }
    queue.submit([encoder.finish()]);
    true
}
fn after(
    probe: Res<Probe>,
    production: Res<super::Pipeline>,
    lights: Res<GlobalClusterableObjectMeta>,
    profiles: Query<Option<&SourceFalloff>, With<ExtractedPointLight>>,
    cache: Res<PipelineCache>,
    device: Res<RenderDevice>,
    queue: Res<RenderQueue>,
    stage: Res<Stage>,
    results: Res<Readbacks>,
) {
    if cache.get_compute_pipeline(production.pipeline).is_none()
        || cache.get_compute_pipeline(probe.before).is_none()
    {
        return;
    }
    if !dispatch(&probe, &lights, &cache, &device, &queue, true) {
        return;
    }
    let (tx, rx) = std::sync::mpsc::channel();
    probe
        .readback
        .slice(..)
        .map_async(MapMode::Read, move |result| {
            tx.send(result).unwrap();
        });
    device
        .poll(PollType::Wait {
            submission_index: None,
            timeout: None,
        })
        .unwrap();
    rx.recv().unwrap().unwrap();
    let data = probe.readback.slice(..).get_mapped_range().unwrap();
    let words: Vec<u32> = data
        .chunks_exact(4)
        .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
        .collect();
    let rows = lights
        .entity_to_index
        .iter()
        .filter_map(|(entity, index)| {
            profiles.get(*entity).ok().map(|profile| Row {
                index: *index,
                expected: profile.map_or(0, |p| p.0),
                words: words[index * 64..index * 64 + 64].to_vec(),
            })
        })
        .collect();
    drop(data);
    probe.readback.unmap();
    results.0.lock().unwrap().push(Frame {
        stage: stage.0,
        rows,
    });
}
