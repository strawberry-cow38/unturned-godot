//! Original InventoryUI wheel/transfer overlays and round-by-round foley.
use super::{controller::Menu, model::*, tiles};
use crate::{
    sound::Bank,
    ui::swoop::{SwoopAlpha, SwoopAnimation},
    world::WorldConfig,
};
use bevy::{
    asset::embedded_asset, audio::Volume, picking::Pickable, prelude::*,
    render::render_resource::AsBindGroup, shader::ShaderRef,
};

#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
pub(super) struct WheelMaterial {
    #[uniform(0)]
    size: Vec4,
    // Fraction, kind(0 magazine/1 transfer), direction(unload/into crate), opacity.
    #[uniform(1)]
    state: Vec4,
}
impl UiMaterial for WheelMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/ui/inventory/wheel.wesl".into()
    }
}
#[derive(Component)]
struct Wheel {
    cell: Cell,
    transfer: bool,
}
#[derive(Resource, Default)]
struct Sounds {
    voice: Option<Entity>,
    seed: u64,
    pistol: Bank,
    rifle: Bank,
    loaded: bool,
}
pub(super) struct FeedbackPlugin;
impl Plugin for FeedbackPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "wheel.wesl");
        app.add_plugins(UiMaterialPlugin::<WheelMaterial>::default())
            .init_resource::<Sounds>()
            .add_systems(Update, audio.after(crate::ui::UiUpdate))
            .add_systems(PostUpdate, wheels.after(SwoopAnimation));
    }
}
pub(super) fn spawn(
    p: &mut ChildSpawnerCommands,
    cell: Cell,
    bounds: Rect,
    transfer: bool,
    materials: &mut Assets<WheelMaterial>,
) {
    p.spawn((
        tiles::rect(bounds),
        Wheel { cell, transfer },
        Pickable::IGNORE,
        SwoopAlpha::default(),
        MaterialNode(materials.add(WheelMaterial {
            size: bounds.size().extend(0.).extend(0.),
            state: Vec4::ZERO,
        })),
    ));
}
fn wheels(
    menu: Res<Menu>,
    nodes: Query<(&Wheel, &MaterialNode<WheelMaterial>, &SwoopAlpha)>,
    mut assets: ResMut<Assets<WheelMaterial>>,
) {
    for (wheel, handle, alpha) in &nodes {
        let Some(mut material) = assets.get_mut(&handle.0) else {
            continue;
        };
        let data = if wheel.transfer {
            menu.transfers
                .queue
                .iter()
                .position(|(cell, _)| *cell == wheel.cell)
                .map(|i| {
                    (
                        if i == 0 {
                            menu.transfers.progress()
                        } else {
                            0.
                        },
                        1.,
                        if !container(wheel.cell.page) { 1. } else { 0. },
                    )
                })
        } else {
            menu.magazines
                .operations
                .iter()
                .find(|op| op.magazine.cell() == wheel.cell)
                .map(|op| {
                    (
                        if op.unloading {
                            1. - op.progress()
                        } else {
                            op.progress()
                        },
                        0.,
                        if op.unloading { 1. } else { 0. },
                    )
                })
        };
        material.state = data.map_or(Vec4::ZERO, |(fraction, kind, direction)| {
            Vec4::new(fraction, kind, direction, alpha.0)
        });
    }
}
fn audio(
    mut commands: Commands,
    mut menu: ResMut<Menu>,
    mut sounds: ResMut<Sounds>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
) {
    if !sounds.loaded {
        sounds.pistol = Bank::load(&config.content_root, "foley", "gun_pistol_load_bullet");
        sounds.rifle = Bank::load(
            &config.content_root,
            "foley",
            "gun_semi_auto_rifle_load_bullet",
        );
        sounds.seed = 0x49564e544f5259;
        sounds.loaded = true;
    }
    // Source reuses one AudioStreamPlayer. A batch ends on its final grab/drop
    // sound, rather than layering every deferred event at the same instant.
    let gestures = std::mem::take(&mut menu.gesture.foley);
    for (width, height) in gestures {
        let light = width < 2 && height < 2;
        let random = crate::particles::random(&mut sounds.seed);
        let count = if light { 7 } else { 6 };
        let variant = (random * count as f32) as usize % count + 1;
        let path = format!(
            "sounds/inv_{}_{variant:02}.wav",
            if light { "light" } else { "heavy" }
        );
        if !config.content_root.join(&path).is_file() {
            continue;
        }
        if let Some(old) = sounds.voice.take() {
            commands.entity(old).try_despawn();
        }
        sounds.voice = Some(
            commands
                .spawn((
                    AudioPlayer::new(assets.load(path)),
                    PlaybackSettings::DESPAWN,
                ))
                .id(),
        );
    }
    for capacity in std::mem::take(&mut menu.round_sounds) {
        crate::particles::random(&mut sounds.seed);
        let seed = sounds.seed;
        let bank = if capacity <= 15 {
            &mut sounds.pistol
        } else {
            &mut sounds.rifle
        };
        let clip = bank.pick(seed).map(str::to_owned);
        let speed = 0.96 + 0.08 * crate::particles::random(&mut sounds.seed);
        if let Some(clip) = clip {
            commands.spawn((
                AudioPlayer::new(assets.load(clip)),
                PlaybackSettings::DESPAWN
                    .with_volume(Volume::Decibels(-9.))
                    .with_speed(speed),
            ));
        }
    }
}
