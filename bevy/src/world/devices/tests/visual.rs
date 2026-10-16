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
