//! `Main.BuildLampTest` (`--lamptest`): one indoor fixture lit over a dark
//! 16 m ground, through the production `LampLight` build and grid apply.
//! `UG_LAMP` = Light_0 (default) / Lamp_1 / Lamp_0, `UG_LAMP_PITCH` (Light_0
//! lies flat on retail ceilings at 270), `UG_LAMPOFF=1` unlit in daylight,
//! `UG_LAMP_OUTLINE=1` the toggle lamp's white look-at rim.
use super::*;
use crate::outline::OutlinePlugin;
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};

#[derive(Resource)]
struct Shot {
    path: String,
    frame: u32,
    done: bool,
    lamp: Option<Entity>,
    outline: bool,
}

#[test]
#[ignore = "Vulkan/display; UG_LAMP_SHOT=PNG, UG_LAMP, UG_LAMP_PITCH, UG_LAMPOFF, UG_LAMP_OUTLINE"]
fn render_source_lamptest_through_the_production_lamp_light() {
    let config = WorldConfig::default();
    let root = config.content_root.canonicalize().unwrap();
    let off = std::env::var("UG_LAMPOFF").as_deref() == Ok("1");
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original lamp test".into(),
                    resolution: (1280, 720).into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: root.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<crate::nightvision::NightVision>()
    .add_plugins((crate::hdr::HdrPlugin, crate::content::ContentPlugin, OutlinePlugin))
    .insert_resource(config)
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .init_resource::<Settings>()
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<InteractionFocus>()
    .insert_resource(Mains { live: !off, ..default() })
    .insert_resource(ClearColor(if off {
        Color::srgb(0.42, 0.52, 0.68)
    } else {
        Color::srgb(0.02, 0.03, 0.05)
    }))
    .insert_resource(GlobalAmbientLight {
        color: if off {
            Color::srgb(0.55, 0.60, 0.68)
        } else {
            Color::srgb(0.035, 0.045, 0.06)
        },
        brightness: crate::hdr::ambient_brightness(1.),
        ..default()
    })
    .insert_resource(Shot {
        path: std::env::var("UG_LAMP_SHOT").unwrap_or_else(|_| "/tmp/bevy-lamptest.png".into()),
        frame: 0,
        done: false,
        lamp: None,
        outline: std::env::var("UG_LAMP_OUTLINE").as_deref() == Ok("1"),
    })
    .configure_sets(Update, crate::entities::ApplyInteractions.after(CollectInteractions))
    .add_systems(Startup, scene)
    .add_systems(Update, (lamps::grid, claim.in_set(CollectInteractions), capture).chain());
    app.run();
}

fn scene(
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mains: Res<Mains>,
    mut shot: ResMut<Shot>,
) {
    let which = std::env::var("UG_LAMP").unwrap_or_else(|_| "Light_0".into());
    let pitch: f32 = std::env::var("UG_LAMP_PITCH").ok().and_then(|v| v.parse().ok()).unwrap_or(0.);
    let off = !mains.live;
    if off {
        // RotationDegrees(-52, 34, 0), energy 1.1.
        commands.spawn((
            DirectionalLight {
                illuminance: crate::hdr::directional_illuminance(1.1),
                ..default()
            },
            Transform::from_rotation(Quat::from_euler(
                EulerRot::YXZ,
                34f32.to_radians(),
                (-52f32).to_radians(),
                0.,
            )),
        ));
    }
    commands.spawn((
        Mesh3d(meshes.add(Plane3d::default().mesh().size(16., 16.))),
        MeshMaterial3d(materials.add(StandardMaterial {
            base_color: Color::srgb(0.30, 0.30, 0.29),
            perceptual_roughness: 1.,
            ..default()
        })),
    ));
    let kind = kind_for(&which);
    let ceiling = which == "Light_0" || which == "Light_1";
    let mount_y = if ceiling { 3. } else { 0. };
    let (mesh, data) = cache
        .load_mesh(&config.content_root.join(format!("objects/{which}.obj")), &mut meshes)
        .unwrap();
    // Roughness 0.85, no culling, nearest texture.
    let base = cache.material(Some(&format!("objects/{which}_tex.png")), false, &assets, &mut materials);
    let mut material = materials.get(&base).unwrap().clone();
    material.perceptual_roughness = 0.85;
    material.double_sided = true;
    material.cull_mode = None;
    let material = materials.add(material);
    let xf = Transform::from_xyz(0., mount_y, 0.).with_rotation(Quat::from_rotation_x(pitch.to_radians()));
    let lamp = commands
        .spawn((Name::new(which.clone()), Mesh3d(mesh), MeshMaterial3d(material.clone()), xf))
        .id();
    let node = Vec3::new(0., mount_y, 0.);
    let mut built = MeshCache::default();
    build(
        &mut commands,
        &mut meshes,
        &mut materials,
        &mut built,
        lamps::Fixture {
            entity: lamp,
            kind,
            index: 0,
            name: &which,
            xf,
            data: &data,
            off: material,
            node,
            jitter_at: node,
            powered: !off,
            on: true,
        },
    );
    shot.lamp = Some(lamp);
    let (eye, target) = if ceiling {
        (Vec3::new(3.4, 0.9, 3.0), Vec3::new(0., mount_y - 0.15, 0.))
    } else if which == "Lamp_1" {
        (Vec3::new(2.2, 1.4, 2.8), Vec3::new(0., 1.2, 0.))
    } else {
        (Vec3::new(0.95, 0.62, 1.25), Vec3::new(0., 0.42, 0.))
    };
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Projection::Perspective(PerspectiveProjection {
            fov: 60f32.to_radians(),
            ..default()
        }),
        Transform::from_translation(eye).looking_at(target, Vec3::Y),
    ));
}

/// `UG_LAMP_OUTLINE=1`: `lamp.SetLookFocused(true)` through the production claim.
fn claim(shot: Res<Shot>, mut focus: ResMut<InteractionFocus>) {
    *focus = default();
    let Some(lamp) = shot.lamp.filter(|_| shot.outline) else { return };
    let args = json!({"sourceIndex":0});
    focus.offer_status(1., "", "toggleLamp", args.clone());
    focus.outline("toggleLamp", &args, lamp, Color::WHITE);
}

fn capture(mut commands: Commands, mut shot: ResMut<Shot>, lamps: Query<&LampFixture>) {
    shot.frame += 1;
    if shot.done || shot.frame < 60 {
        return;
    }
    let lamp = lamps.single().unwrap();
    eprintln!(
        "[lamplight] kind={:?} gpos={:?} energy={} lit={}",
        lamp.kind,
        lamp.light_world,
        lamp.omni_energy,
        lamp.effective()
    );
    shot.done = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.path.clone()))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}

#[derive(Resource)]
struct NoteShot(String, u32, bool);

/// `NoteReader.Show` on Washington's "Note - Military #1" (`UG_NOTE_GUID`),
/// through the production `notes::draw`, over a dark backdrop.
#[test]
#[ignore = "Vulkan/display; UG_NOTE_SHOT=PNG, UG_NOTE_GUID"]
fn render_source_note_reader_panel() {
    let config = WorldConfig::default();
    let texts = Texts::parse(&std::fs::read_to_string(config.content_root.join("note_texts.tsv")).unwrap());
    let guid = std::env::var("UG_NOTE_GUID").unwrap_or_else(|_| "53f724bd841b42b2839d476f07e1d3de".into());
    let (name, lines) = texts[&guid].clone();
    let note = Note { name, lines };
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original note reader".into(),
                    resolution: (1280, 720).into(),
                    ..default()
                }),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<crate::character::nameplate::OriginalFont>()
    .insert_resource(Reader { open: Some((note.title(), note.body())) })
    .insert_resource(ClearColor(Color::srgb(0.02, 0.03, 0.05)))
    .insert_resource(NoteShot(
        std::env::var("UG_NOTE_SHOT").unwrap_or_else(|_| "/tmp/bevy-note.png".into()),
        0,
        false,
    ))
    .add_systems(Startup, |mut commands: Commands| {
        commands.spawn(Camera2d);
    })
    .add_systems(Update, (notes::draw, note_capture).chain());
    app.run();
}
fn note_capture(mut commands: Commands, mut shot: ResMut<NoteShot>) {
    shot.1 += 1;
    if shot.2 || shot.1 < 30 {
        return;
    }
    shot.2 = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.0.clone()))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}

#[derive(Resource)]
struct PropShot {
    path: String,
    frame: u32,
    done: bool,
    prop: Option<Entity>,
}

/// `Main.BuildPropTest` (`--proptest=NAME UG_LIVE=1`) through the production
/// device systems: `UG_PROP` (Science_3 / Radio_0 / Vendor_0), `UG_PROPROT`
/// (Godot RotationDegrees), `UG_CAMPOS`/`UG_CAMLOOK`, `UG_PROP_SHOT`;
/// `UG_FLATLINE=1` / `UG_MONITOR_OFF=1` for the monitor, `UG_FORECAST` =
/// "today|tomorrow" for the radio board, `UG_VEND_FOCUS=1` for the machine's
/// look-at rim and billboard (no wallet: "Insufficient funds").
#[test]
#[ignore = "Vulkan/display; UG_PROP, UG_PROPROT, UG_CAMPOS, UG_CAMLOOK, UG_PROP_SHOT"]
fn render_source_proptest_device() {
    let config = WorldConfig::default();
    let root = config.content_root.canonicalize().unwrap();
    let mut forecast = std::env::var("UG_FORECAST").unwrap_or_else(|_| "Clear|Clear".into());
    if !forecast.contains('|') {
        forecast.push('|');
    }
    let (today, tomorrow) = forecast.split_once('|').unwrap();
    let wet = |n: &str| json!({"wet": n != "Clear", "name": n});
    let snapshot = json!({"player":{"alive":true},"weather":{"today":wet(today),"tomorrow":wet(tomorrow)}});
    let mut model = crate::ui::UiModel::default();
    model.adopt_snapshot(&snapshot);
    let mut state = DeviceState::default();
    state.radio_on.insert(1);
    if std::env::var("UG_MONITOR_OFF").as_deref() == Ok("1") {
        state.monitor_off.insert(1);
    }
    let mut app = App::new();
    app.add_plugins(
        DefaultPlugins
            .set(WindowPlugin {
                primary_window: Some(Window {
                    title: "Original prop test".into(),
                    resolution: (900, 900).into(),
                    ..default()
                }),
                ..default()
            })
            .set(AssetPlugin {
                file_path: root.to_string_lossy().into_owned(),
                ..default()
            })
            .set(WinitPlugin {
                run_on_any_thread: true,
                ..default()
            }),
    )
    .init_resource::<crate::nightvision::NightVision>()
    .add_plugins((
        crate::hdr::HdrPlugin,
        crate::content::ContentPlugin,
        OutlinePlugin,
        crate::info_billboard::InfoBillboardPlugin,
        MaterialPlugin::<crate::character::nameplate::PlateMaterial>::default(),
    ))
    .init_resource::<crate::character::nameplate::OriginalFont>()
    .insert_resource(config)
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .insert_resource(GameSnapshot(snapshot))
    .insert_resource(model)
    .insert_resource(state)
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<InteractionFocus>()
    .init_resource::<Mains>()
    .init_resource::<monitor::Clock>()
    .insert_resource(ClearColor(Color::srgb(0.32, 0.36, 0.44)))
    .insert_resource(GlobalAmbientLight {
        color: Color::srgb(0.7, 0.7, 0.72),
        brightness: crate::hdr::ambient_brightness(0.9),
        ..default()
    })
    .insert_resource(PropShot {
        path: std::env::var("UG_PROP_SHOT").unwrap_or_else(|_| "/tmp/bevy-proptest.png".into()),
        frame: 0,
        done: false,
        prop: None,
    })
    .configure_sets(Update, crate::entities::ApplyInteractions.after(CollectInteractions))
    .add_systems(Startup, prop_scene)
    .add_systems(
        Update,
        (
            (vending::attach, radio::attach, monitor::attach, carlift::attach),
            prop_force,
            ((vending::shake, vending::tint).chain(), monitor::tick),
            prop_claim.in_set(CollectInteractions),
            (vending::prompt, radio::board).in_set(crate::entities::ApplyInteractions),
            prop_capture,
        )
            .chain(),
    );
    super::super::register_ecg(&mut app);
    app.run();
}

fn prop_scene(
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut shot: ResMut<PropShot>,
) {
    let name = std::env::var("UG_PROP").unwrap_or_else(|_| "Science_3".into());
    let v3 = |key: &str, fallback: Vec3| {
        std::env::var(key)
            .ok()
            .and_then(|v| {
                let n: Vec<f32> = v.trim_matches('"').split(',').filter_map(|s| s.parse().ok()).collect();
                (n.len() == 3).then(|| Vec3::new(n[0], n[1], n[2]))
            })
            .unwrap_or(fallback)
    };
    let rot = v3("UG_PROPROT", Vec3::new(270., 0., 0.));
    // RotationDegrees(rx, ry, rz) == placement_transform's Y(180 - ey) X(ex) Z(-ez).
    let euler = Vec3::new(rot.x, 180. - rot.y, -rot.z);
    // BuildPropTest's DirectionalLight3D RotationDegrees(-45, -35, 0), energy 1.2.
    commands.spawn((
        DirectionalLight {
            illuminance: crate::hdr::directional_illuminance(1.2),
            ..default()
        },
        Transform::from_rotation(Quat::from_euler(EulerRot::YXZ, (-35f32).to_radians(), (-45f32).to_radians(), 0.)),
    ));
    let (mesh, _) = cache.load_mesh(&config.content_root.join(format!("objects/{name}.obj")), &mut meshes).unwrap();
    let base = cache.material(Some(&format!("objects/{name}_tex.png")), false, &assets, &mut materials);
    let mut material = materials.get(&base).unwrap().clone();
    material.perceptual_roughness = 1.;
    material.double_sided = true;
    material.cull_mode = None;
    let xf = crate::world::placement_transform(Vec3::ZERO, euler, Vec3::ONE);
    let prop = commands
        .spawn((
            Name::new(name.clone()),
            Mesh3d(mesh),
            MeshMaterial3d(materials.add(material)),
            xf,
            WorldObject { guid: String::new(), asset_name: name.clone(), source_index: 1 },
            PlacementEuler(euler),
        ))
        .id();
    shot.prop = Some(prop);
    let eye = v3("UG_CAMPOS", Vec3::new(0.9, 1.7, -2.));
    let look = v3("UG_CAMLOOK", Vec3::new(0., 1.45, 0.));
    commands.spawn((
        Camera3d::default(),
        crate::player::PlayerCamera,
        Projection::Perspective(PerspectiveProjection {
            fov: 50f32.to_radians(),
            ..default()
        }),
        Transform::from_translation(eye).looking_at(look, Vec3::Y),
    ));
}

/// `HeartMonitor.Make(propMi, alive: UG_FLATLINE != 1)`: the harness picks
/// the state the world build would roll.
fn prop_force(mut monitors: Query<&mut monitor::Monitor>) {
    let alive = std::env::var("UG_FLATLINE").as_deref() != Ok("1");
    for mut m in &mut monitors {
        if m.alive != alive {
            m.alive = alive;
        }
    }
}

/// `UG_VEND_FOCUS=1`: the machine as the look winner (rim + billboard).
fn prop_claim(shot: Res<PropShot>, mut focus: ResMut<InteractionFocus>, tints: Query<&DeviceTint>) {
    *focus = default();
    let Some(prop) = shot.prop.filter(|_| std::env::var("UG_VEND_FOCUS").as_deref() == Ok("1")) else {
        return;
    };
    let (action, args) = Target::Vendor(1).action();
    focus.offer_status(1., "", action, args.clone());
    let tint = tints.get(prop).map_or(Color::WHITE, |t| t.0);
    focus.outline(action, &args, prop, tint);
}

fn prop_capture(mut commands: Commands, mut shot: ResMut<PropShot>, monitors: Query<&monitor::Monitor>) {
    shot.frame += 1;
    if shot.done || shot.frame < 60 {
        return;
    }
    if let Ok(m) = monitors.single() {
        eprintln!("[proptest] monitor alive={} lit={:?}", m.alive, m.lit);
    }
    shot.done = true;
    commands
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(shot.path.clone()))
        .observe(|_: On<ScreenshotCaptured>, mut exit: MessageWriter<AppExit>| {
            exit.write(AppExit::Success);
        });
}
