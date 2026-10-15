//! Source WeatherManager strike envelope, cloud glow and delayed thunder pool.
mod render;
use super::RainUpdate;
use crate::{
    entities::CoreEvent,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::Settings,
    ui::UiState,
    world::WorldConfig,
};
use bevy::{
    asset::embedded_asset,
    audio::{AudioSinkPlayback, Volume},
    prelude::*,
    render::{
        RenderApp,
        extract_component::{ExtractComponent, ExtractComponentPlugin, UniformComponentPlugin},
        render_resource::ShaderType,
    },
};
use std::collections::VecDeque;

#[derive(Clone, Copy)]
struct Boom {
    remaining: f32,
    db: f32,
    pick: usize,
}
#[derive(Resource)]
pub struct Lightning {
    pub flash: f32,
    pub tint: Vec3,
    pub direction: Vec3,
    distance: f32,
    strokes: u8,
    reflash: f32,
    next: f32,
    random: u64,
    pending: Vec<Boom>,
    tick: Option<u64>,
    epoch: u64,
    elapsed: f32,
    diagnostic: VecDeque<f32>,
    pub strikes: u64,
}
impl Default for Lightning {
    fn default() -> Self {
        Self {
            flash: 0.,
            tint: Vec3::ONE,
            direction: Vec3::NEG_Z,
            distance: 0.,
            strokes: 0,
            reflash: -1.,
            next: -1.,
            random: 182947,
            pending: vec![],
            tick: None,
            epoch: u64::MAX,
            elapsed: 0.,
            diagnostic: std::env::var("UG_STRIKE_AT")
                .unwrap_or_default()
                .split(',')
                .filter_map(|s| s.parse::<f32>().ok().filter(|v| v.is_finite() && *v >= 0.))
                .collect(),
            strikes: 0,
        }
    }
}
impl Lightning {
    fn strike(&mut self) {
        self.flash = 0.9;
        self.distance = crate::particles::random(&mut self.random);
        let az = crate::particles::random(&mut self.random) * std::f32::consts::TAU;
        self.direction = Vec3::new(az.cos(), 0., az.sin());
        let count = if self.distance < 0.4 {
            if crate::particles::random(&mut self.random) < 0.5 {
                3
            } else {
                2
            }
        } else if self.distance < 0.72 && crate::particles::random(&mut self.random) < 0.5 {
            2
        } else {
            1
        };
        self.strokes = count - 1;
        self.reflash = if self.strokes > 0 {
            0.04 + 0.06 * crate::particles::random(&mut self.random)
        } else {
            -1.
        };
        self.pending.push(Boom {
            remaining: 0.24 + 2.16 * self.distance,
            db: -0.5 - 8. * self.distance,
            pick: if self.distance < 0.4 {
                1
            } else if self.distance > 0.72 {
                2
            } else {
                0
            },
        });
        self.strikes += 1;
    }
    fn advance(&mut self, dt: f32) -> Vec<Boom> {
        if self.strokes > 0 {
            self.reflash -= dt;
            if self.reflash <= 0. {
                self.flash = 1.3;
                self.strokes -= 1;
                self.reflash = if self.strokes > 0 {
                    0.04 + 0.06 * crate::particles::random(&mut self.random)
                } else {
                    -1.
                };
            }
        }
        if self.flash > 0. {
            self.flash *= (-7. * dt).exp();
            if self.flash < 0.003 {
                self.flash = 0.;
            }
        }
        let warmth = ((1. - self.flash.clamp(0., 1.)) * 0.55 + self.distance * 0.5).clamp(0., 1.);
        self.tint = Vec3::new(1., 0.98, 0.94).lerp(Vec3::new(1., 0.90, 0.72), warmth);
        let mut ready = Vec::new();
        self.pending.retain_mut(|boom| {
            boom.remaining -= dt;
            if boom.remaining <= 0. {
                ready.push(*boom);
                false
            } else {
                true
            }
        });
        // Source removes ready entries while walking its queue backwards.
        ready.reverse();
        ready
    }
}
#[derive(Component, Clone, Copy, ExtractComponent, ShaderType)]
#[extract_app(RenderApp)]
struct FlashUniform {
    color: Vec4,
}
#[derive(Component)]
pub(crate) struct ConsoleCamera;
#[derive(Component)]
struct ThunderVoice(f32);
#[derive(Resource, Default)]
struct Thunder {
    clips: Vec<Option<Handle<AudioSource>>>,
    voices: [Option<Entity>; 4],
    next: usize,
}
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct LightningUpdate;
pub struct LightningPlugin;
impl Plugin for LightningPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "lightning.wesl");
        app.add_plugins((
            ExtractComponentPlugin::<FlashUniform>::default(),
            UniformComponentPlugin::<FlashUniform>::default(),
        ))
        .init_resource::<Lightning>()
        .init_resource::<Thunder>()
        .add_systems(Startup, setup)
        .add_systems(Update, update.after(RainUpdate).in_set(LightningUpdate))
        .add_systems(Update, route_console.after(crate::ui::UiUpdate))
        .add_systems(
            PostUpdate,
            route_flash.after(crate::ui::backdrop::BackdropRouting),
        );
        render::install(app);
    }
}
fn setup(
    mut commands: Commands,
    mut thunder: ResMut<Thunder>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
) {
    thunder.clips = ["thunder.wav", "thunder2.wav", "thunder3.wav"]
        .iter()
        .map(|p| {
            config
                .content_root
                .join(p)
                .is_file()
                .then(|| assets.load((*p).to_owned()))
        })
        .collect();
    // Source appends retail rumbles to its pool, while its distance selector
    // currently chooses indices0..2. Keep that actual selection contract.
    for path in crate::sound::Bank::load(
        &config.content_root,
        "ambience",
        "thunder_lightning_strike_rumble",
    )
    .clips
    {
        thunder.clips.push(Some(assets.load(path)));
    }
    // Source DevConsole layer100 stays above WeatherManager's flash layer90.
    commands.spawn((
        Name::new("Source console layer100 camera"),
        Camera2d,
        Camera {
            order: 4,
            clear_color: ClearColorConfig::None,
            ..default()
        },
        ConsoleCamera,
    ));
}
fn route_console(
    mut commands: Commands,
    state: Res<UiState>,
    camera: Query<Entity, With<ConsoleCamera>>,
    roots: Query<Entity, With<crate::ui::OverlayRoot>>,
) {
    let Ok(camera) = camera.single() else { return };
    for root in &roots {
        if state.screen == crate::ui::Screen::Console {
            commands.entity(root).insert(UiTargetCamera(camera));
        } else {
            commands.entity(root).remove::<UiTargetCamera>();
        }
    }
}

fn update(
    mut commands: Commands,
    mut flash: ResMut<Lightning>,
    mut thunder: ResMut<Thunder>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    mut facts: MessageReader<CoreEvent>,
    mut voices: Query<(&ThunderVoice, &mut PlaybackSettings, Option<&mut AudioSink>)>,
) {
    if flash.epoch != epoch.0 {
        for e in thunder.voices.iter().flatten() {
            commands.entity(*e).try_despawn();
        }
        thunder.voices = [None; 4];
        *flash = Lightning {
            epoch: epoch.0,
            ..default()
        };
        if std::env::var("UG_WEATHER").as_deref() == Ok("lightning") {
            flash.strike();
        }
    }
    let paused = crate::player::simulation_paused(&ui);
    let dt = if paused { 0. } else { time.delta_secs() };
    let tick = snapshot.0["tick"].as_u64().unwrap_or(0);
    let weather_dt = flash
        .tick
        .map_or(0., |last| tick.saturating_sub(last) as f32 / 50.);
    flash.tick = Some(tick);
    let weather = &snapshot.0["weather"];
    if !paused {
        let strike = weather["hasLightning"].as_bool().unwrap_or(false)
            && weather["blendAlpha"].as_f64().unwrap_or(0.) > 0.5;
        if !strike {
            flash.next = -1.;
        } else {
            let min = weather["minLightningInterval"].as_f64().unwrap_or(15.) as f32;
            let max = weather["maxLightningInterval"].as_f64().unwrap_or(60.) as f32;
            if flash.next < 0. {
                flash.next = min + (max - min) * crate::particles::random(&mut flash.random);
            }
            flash.next -= weather_dt;
            if flash.next <= 0. {
                flash.strike();
                flash.next = min + (max - min) * crate::particles::random(&mut flash.random);
            }
        }
        flash.elapsed += dt;
        while flash
            .diagnostic
            .front()
            .is_some_and(|t| *t <= flash.elapsed)
        {
            flash.diagnostic.pop_front();
            flash.strike();
        }
    }
    for event in facts.read() {
        if event.kind == "weatherStrike" {
            flash.strike();
        }
    }
    let ready = flash.advance(dt);
    for boom in ready {
        let source = thunder
            .clips
            .get(boom.pick)
            .and_then(Option::as_ref)
            .or_else(|| thunder.clips.iter().take(3).flatten().next())
            .cloned();
        let Some(source) = source else { continue };
        let index = thunder.next;
        thunder.next = (index + 1) % 4;
        if let Some(old) = thunder.voices[index].take() {
            commands.entity(old).try_despawn();
        }
        let gain = 10_f32.powf(boom.db / 20.);
        let entity = commands
            .spawn((
                ThunderVoice(gain),
                AudioPlayer::new(source),
                PlaybackSettings::DESPAWN
                    .with_volume(Volume::Linear(gain * settings.master_volume)),
            ))
            .id();
        thunder.voices[index] = Some(entity);
    }
    for (sound, mut playback, sink) in &mut voices {
        let volume = Volume::Linear(sound.0 * settings.master_volume);
        playback.volume = volume;
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
            if paused { sink.pause() } else { sink.play() }
        }
    }
}
fn route_flash(
    mut commands: Commands,
    flash: Res<Lightning>,
    overlay: Query<
        (Entity, Has<crate::ui::backdrop::ModalCamera>),
        Or<(
            With<IsDefaultUiCamera>,
            With<crate::ui::backdrop::ModalCamera>,
        )>,
    >,
) {
    // Layer90 follows both the ordinary HUD and modal layer60. Headless/test
    // callers without the modal plugin keep their original default-camera path.
    let modal_exists = overlay.iter().any(|(_, modal)| modal);
    for (entity, modal) in &overlay {
        if modal_exists && !modal {
            commands.entity(entity).remove::<FlashUniform>();
            continue;
        }
        commands.entity(entity).insert(FlashUniform {
            color: flash.tint.extend(if flash.flash > 0.001 {
                flash.flash.min(1.2) * 0.09
            } else {
                0.
            }),
        });
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn console_is_above_flash_while_gameplay_overlays_use_default_camera() {
        let mut app = App::new();
        app.init_resource::<UiState>()
            .add_systems(Update, route_console);
        let console = app.world_mut().spawn(ConsoleCamera).id();
        let panel = app.world_mut().spawn(crate::ui::OverlayRoot).id();
        let hud = app.world_mut().spawn(Node::default()).id();
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Console;
        app.update();
        assert_eq!(app.world().get::<UiTargetCamera>(panel).unwrap().0, console);
        assert!(app.world().get::<UiTargetCamera>(hud).is_none());
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
        app.update();
        assert!(app.world().get::<UiTargetCamera>(panel).is_none());
    }
    #[test]
    fn flash_follows_modal_ui_and_returns_to_default_if_camera_is_removed() {
        let mut app = App::new();
        app.init_resource::<Lightning>()
            .add_systems(PostUpdate, route_flash);
        let hud = app.world_mut().spawn(IsDefaultUiCamera).id();
        app.world_mut().resource_mut::<Lightning>().flash = 1.;
        app.update();
        assert!(app.world().get::<FlashUniform>(hud).is_some());
        let modal = app.world_mut().spawn(crate::ui::backdrop::ModalCamera).id();
        app.update();
        assert!(app.world().get::<FlashUniform>(hud).is_none());
        assert_eq!(
            app.world().get::<FlashUniform>(modal).unwrap().color.w,
            0.09
        );
        app.world_mut().entity_mut(modal).despawn();
        app.update();
        assert!(app.world().get::<FlashUniform>(hud).is_some());
    }
    #[test]
    fn retained_strike_drives_overlay_thunder_pause_and_session_reset() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<AudioSource>()
            .init_resource::<Lightning>()
            .init_resource::<Thunder>()
            .init_resource::<WorldSessionEpoch>()
            .init_resource::<Settings>()
            .insert_resource(GameSnapshot(serde_json::json!({"tick":0})))
            .insert_resource(UiState {
                screen: crate::ui::Screen::Playing,
                ..default()
            })
            .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
                std::time::Duration::from_millis(20),
            ))
            .add_message::<CoreEvent>()
            .add_systems(Update, update)
            .add_systems(PostUpdate, route_flash);
        let clip = app
            .world_mut()
            .resource_mut::<Assets<AudioSource>>()
            .add(AudioSource {
                bytes: std::sync::Arc::from([]),
            });
        app.world_mut().resource_mut::<Thunder>().clips = vec![Some(clip); 3];
        let overlay = app.world_mut().spawn(IsDefaultUiCamera).id();
        app.update();
        app.world_mut().write_message(CoreEvent {
            sequence: 1,
            kind: "weatherStrike".into(),
            data: serde_json::Value::Null,
        });
        app.update();
        assert_eq!(app.world().resource::<Lightning>().strikes, 1);
        assert!(app.world().get::<FlashUniform>(overlay).unwrap().color.w > 0.);
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
        let before = app.world().resource::<Lightning>().flash;
        for _ in 0..60 {
            app.update();
        }
        assert_eq!(app.world().resource::<Lightning>().flash, before);
        assert_eq!(
            app.world_mut()
                .query::<&ThunderVoice>()
                .iter(app.world())
                .count(),
            0
        );
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
        for _ in 0..140 {
            app.update();
        }
        assert_eq!(
            app.world().get::<FlashUniform>(overlay).unwrap().color.w,
            0.
        );
        assert_eq!(
            app.world_mut()
                .query::<&ThunderVoice>()
                .iter(app.world())
                .count(),
            1
        );
        app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
        app.update();
        assert!(
            app.world_mut()
                .query_filtered::<&PlaybackSettings, With<ThunderVoice>>()
                .iter(app.world())
                .all(|p| p.paused)
        );
        app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
        app.update();
        assert_eq!(
            app.world_mut()
                .query::<&ThunderVoice>()
                .iter(app.world())
                .count(),
            0
        );
    }
    #[test]
    fn overlapping_strikes_keep_both_thunder_delays_and_fade_on_render_clock() {
        let mut f = Lightning::default();
        f.strike();
        f.strike();
        assert_eq!(f.pending.len(), 2);
        let old = f.flash;
        assert!(f.advance(0.).is_empty());
        assert_eq!(f.flash, old);
        let mut heard = 0;
        for _ in 0..180 {
            heard += f.advance(1. / 60.).len();
        }
        assert_eq!(heard, 2);
        assert_eq!(f.flash, 0.);
        assert!(f.tint.is_finite());
        assert!((f.direction.length() - 1.).abs() < 1e-6);
    }
    #[test]
    fn source_distance_selects_matching_clap_and_gain() {
        let mut f = Lightning::default();
        for _ in 0..50 {
            f.strike();
            let b = f.pending.last().unwrap();
            assert!((0.24..=2.4).contains(&b.remaining));
            assert!((-8.5..=-0.5).contains(&b.db));
            assert_eq!(
                b.pick,
                if f.distance < 0.4 {
                    1
                } else if f.distance > 0.72 {
                    2
                } else {
                    0
                }
            );
        }
        assert_eq!(f.pending.len(), 50);
    }
}
