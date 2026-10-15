//! Production portrait renderer with fixed accepted owner rows. This proves art,
//! framing and render isolation, not a live inventory transaction journey.
use super::*;
use crate::{
    player::{GameSnapshot, WorldSessionEpoch},
    world::WorldConfig,
};
use bevy::{
    app::AppExit,
    render::view::screenshot::{Screenshot, ScreenshotCaptured, save_to_disk},
    winit::WinitPlugin,
};
use serde_json::{Value, json};
use std::sync::{Arc, Mutex};

#[derive(Resource)]
struct Capture {
    prefix: String,
    stage: usize,
    frames: u32,
    pending: bool,
    done: Arc<Mutex<bool>>,
    facts: Vec<Value>,
    bow: u16,
}
#[derive(Component)]
struct Picture;

#[test]
#[ignore = "requires Vulkan/display; UG_PAPERDOLL_SHOT sets output prefix"]
fn gpu_original_portrait_filmic_rifle_bow_and_night_room_light() {
    let prefix =
        std::env::var("UG_PAPERDOLL_SHOT").unwrap_or_else(|_| "/tmp/bevy-paperdoll".into());
    let config = WorldConfig::default();
    let catalog = crate::avatar::remotes::tests::source_catalog();
    let bow = *catalog
        .items
        .iter()
        .find(|(_, i)| i.asset.as_deref() == Some("bow_compound"))
        .unwrap()
        .0;
    let mut app = App::new();
    app.add_plugins(DefaultPlugins
        .set(AssetPlugin { file_path: config.content_root.canonicalize().unwrap().to_string_lossy().into(), ..default() })
        .set(WindowPlugin { primary_window: Some(Window { title: "Original Inventory paperdoll production fixture".into(), resolution: (800,720).into(), ..default() }), ..default() })
        .set(WinitPlugin { run_on_any_thread: true, ..default() }))
        .insert_resource(config)
        .insert_resource(catalog)
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<NearbyLight>()
        .insert_resource(GameSnapshot(json!({"connected":true,"player":{
            "heldItemId":4,"stance":"prone","position":[500,90,-500],"profile":{"face":19}},
            "inventory":{"worn":{"shirt":24,"pants":23,"hat":27},"wornQuality":{"shirt":75,"pants":100}},
            "weapon":{"attachments":{"sightId":21,"barrelId":7,"magazineId":6}}})))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .add_plugins((crate::content::ContentPlugin, crate::rig::RigPlugin, crate::character::CharacterPlugin,
            super::super::PaperdollPlugin, PortraitViewPlugin))
        .insert_resource(Capture { prefix: prefix.clone(), stage:0, frames:0, pending:false, done:default(), facts:vec![], bow })
        .add_systems(Startup, scene)
        .add_systems(Update, picture)
        .add_systems(Last, capture);
    app.world_mut().resource_mut::<Portrait>().active = true;
    app.world_mut().resource_mut::<Surface>().size = UVec2::new(616, 444);
    app.run();
    let facts: Vec<Value> =
        serde_json::from_slice(&std::fs::read(format!("{prefix}.json")).unwrap()).unwrap();
    assert_eq!(facts.len(), 3);
    assert_eq!(facts[0]["hook"], "Right_Hook");
    assert_eq!(facts[1]["hook"], "Left_Hook");
    assert_eq!(facts[0]["camera"], facts[1]["camera"]);
    assert_eq!(facts[1]["camera"], facts[2]["camera"]);
    assert_eq!(facts[0]["clip"], "Idle_Stand");
    assert!((facts[2]["key"].as_f64().unwrap() - 0.38).abs() < 1e-5);
    let decode = |stage| {
        Image::from_buffer(
            &std::fs::read(format!("{prefix}-{stage}.png")).unwrap(),
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
    };
    let images = [decode(0), decode(1), decode(2)];
    assert!(
        images[0] != images[1],
        "accepted bow must replace the rifle in the rendered portrait"
    );
    assert!(
        images[1] != images[2],
        "source night/room light must change the actual portrait pixels"
    );
    // Empty viewport pixels preserve the background checker; a failed shader
    // or an opaque viewport clear must not turn the portrait into a black box.
    for image in &images {
        assert_eq!(image.get_pixel(20, 180), image.get_pixel(20, 52));
        let corner = image.get_pixel(20, 180).0;
        let distinct = (170..590)
            .flat_map(|y| (160..480).map(move |x| (x, y)))
            .filter(|(x, y)| {
                let c = image.get_pixel(*x, *y).0;
                c.iter().zip(corner).any(|(a, b)| a.abs_diff(b) > 45)
            })
            .count();
        assert!(
            distinct > 2000,
            "original rig must draw substantial non-background pixels, got {distinct}"
        );
    }
}
fn scene(mut commands: Commands) {
    commands.spawn((
        Camera2d,
        Camera {
            order: 2,
            clear_color: ClearColorConfig::Custom(Color::srgb(0.12, 0.14, 0.17)),
            ..default()
        },
        IsDefaultUiCamera,
        Hdr,
        Tonemapping::None,
        DebandDither::Disabled,
    ));
    commands.insert_resource(GlobalAmbientLight {
        color: Color::srgb(0.55, 0.57, 0.6),
        brightness: crate::hdr::ambient_brightness(0.6),
        ..default()
    });
    commands.spawn((
        crate::environment::Sun,
        DirectionalLight {
            color: Color::WHITE,
            illuminance: crate::hdr::directional_illuminance(1.),
            ..default()
        },
        Transform::default(),
        RenderLayers::layer(0),
    ));
    for y in 0..12 {
        for x in 0..13 {
            let gray = if (x + y) % 2 == 0 { 0.12 } else { 0.18 };
            commands.spawn((
                Node {
                    position_type: PositionType::Absolute,
                    left: px(x as f32 * 64.),
                    top: px(y as f32 * 64.),
                    width: px(64.),
                    height: px(64.),
                    ..default()
                },
                BackgroundColor(Color::srgb(gray, gray + 0.02, gray + 0.05)),
            ));
        }
    }
    commands.spawn((
        Text::new("Original Inventory portrait: accepted clothes and held item"),
        TextFont {
            font_size: FontSize::Px(19.),
            ..default()
        },
        TextColor(Color::srgb(0.88, 0.88, 0.91)),
        Node {
            position_type: PositionType::Absolute,
            left: px(20.),
            top: px(20.),
            ..default()
        },
    ));
}
fn picture(mut commands: Commands, surface: Res<Surface>, pictures: Query<Entity, With<Picture>>) {
    if !pictures.is_empty() {
        return;
    }
    let Some(material) = surface.material.clone() else {
        return;
    };
    commands.spawn((
        Picture,
        Node {
            position_type: PositionType::Absolute,
            left: px(12.),
            top: px(154.),
            width: px(616.),
            height: px(444.),
            ..default()
        },
        MaterialNode(material),
    ));
}
fn capture(world: &mut World) {
    let mut state = world.remove_resource::<Capture>().unwrap();
    if state.pending {
        if !*state.done.lock().unwrap() {
            world.insert_resource(state);
            return;
        }
        *state.done.lock().unwrap() = false;
        state.stage += 1;
        state.frames = 0;
        state.pending = false;
        if state.stage == 3 {
            std::fs::write(
                format!("{}.json", state.prefix),
                serde_json::to_vec_pretty(&state.facts).unwrap(),
            )
            .unwrap();
            world.write_message(AppExit::Success);
            world.insert_resource(state);
            return;
        }
        if state.stage == 1 {
            world.resource_mut::<GameSnapshot>().0["player"]["heldItemId"] = json!(state.bow);
            world.resource_mut::<GameSnapshot>().0["weapon"]["attachments"] = json!({});
        } else {
            let mut q =
                world.query_filtered::<&mut DirectionalLight, With<crate::environment::Sun>>();
            q.single_mut(world).unwrap().illuminance = crate::hdr::directional_illuminance(0.1);
            world.resource_mut::<GlobalAmbientLight>().brightness =
                crate::hdr::ambient_brightness(0.1);
            *world.resource_mut::<NearbyLight>() = NearbyLight {
                energy: 1.5,
                color: Color::srgb(1., 0.3, 0.1),
            };
        }
    }
    state.frames += 1;
    if state.frames == 55 {
        let root = world.resource::<Portrait>().root.unwrap();
        let rig = world.get::<RigPlayback>(root).unwrap();
        let held = world.get::<crate::avatar::held_item::Held>(root).unwrap();
        let hook = world.get::<ChildOf>(held.root.unwrap()).unwrap().parent();
        let hook = rig
            .bones
            .iter()
            .find(|(_, e)| **e == hook)
            .unwrap()
            .0
            .clone();
        let camera = world.resource::<Surface>().camera.unwrap();
        let light = world.resource::<Lighting>();
        state.facts.push(json!({"stage":state.stage,"heldId":held.id,"hook":hook,"clip":rig.clip,
            "camera":world.get::<Transform>(camera).unwrap().translation.to_array(),"size":world.resource::<Surface>().size.to_array(),
            "key":light.key_energy,"fill":light.fill_energy,"fillColor":light.fill.to_srgba().to_f32_array(),"ambient":light.ambient_energy}));
        let done = state.done.clone();
        world
            .spawn(Screenshot::primary_window())
            .observe(save_to_disk(format!(
                "{}-{}.png",
                state.prefix, state.stage
            )))
            .observe(move |_: On<ScreenshotCaptured>| {
                *done.lock().unwrap() = true;
            });
        state.pending = true;
    }
    world.insert_resource(state);
}
