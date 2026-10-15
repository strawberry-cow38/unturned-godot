//! Continuous melee presentation from PlayerController.UpdateSalvage/UpdateChainsaw
//! and Viewmodel.StartTorch/SetTorchSparks. Damage remains on the original server.
use super::*;
use crate::particles::{self, Emitter, ParticleAssets, ParticleUpdate, Spec};
use bevy::audio::{AudioSinkPlayback, SpatialScale, Volume};

#[derive(Clone, Copy, Default, PartialEq, Eq, Debug)]
pub(super) enum Kind {
    #[default]
    None,
    Torch,
    Saw,
}
impl Kind {
    pub(super) fn of(item: Option<&crate::entities::ItemVisual>) -> Self {
        match item {
            Some(i) if i.melee["repeated"].as_bool() == Some(true) => {
                if i.melee["repair"].as_bool() == Some(true) {
                    Self::Torch
                } else {
                    Self::Saw
                }
            }
            _ => Self::None,
        }
    }
}
pub(super) fn set_active(rig: &mut RigPlayback, asset: &str, active: bool) {
    let clip = if active {
        own_clip(rig, asset, "_Start_Swing", "")
    } else {
        own_clip(
            rig,
            asset,
            "_Stop_Swing",
            &own_clip(rig, asset, "_Equip", "Melee_Equip"),
        )
    };
    // Source explicitly disables Start_Swing looping and holds its final pose.
    // Subsequent accepted damage events must not restart this animation.
    play_once(rig, &clip, 1.);
}
pub(super) fn shake(seed: &mut u64, cutting: bool) -> Vec3 {
    *seed = (*seed).max(1);
    let amplitude = if cutting {
        Vec3::new(0.0075, 0.0075, 0.0042)
    } else {
        Vec3::new(0.0016, 0.0016, 0.0009)
    };
    (Vec3::new(
        particles::random(seed),
        particles::random(seed),
        particles::random(seed),
    ) * 2.
        - Vec3::ONE)
        * amplitude
}
#[derive(Resource, Default)]
struct Feedback {
    voice: Option<(Kind, Entity)>,
    sparks: Option<(Entity, Entity)>,
    material: Option<Handle<StandardMaterial>>,
    saw_clock: f32,
    seed: u64,
}
#[derive(Component)]
struct ToolLoop(Kind);
#[derive(Component)]
struct Nozzle {
    gun: Entity,
    frame: Entity,
}
pub(super) struct ToolPlugin;
impl Plugin for ToolPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Feedback>()
            .add_systems(Update, feedback.after(animate_viewmodel))
            .add_systems(
                PostUpdate,
                (aim_sparks, update_loops)
                    .after(bevy::transform::TransformSystems::Propagate)
                    .before(ParticleUpdate),
            );
    }
}
fn spark_spec(frame: Entity, material: Handle<StandardMaterial>) -> Spec {
    Spec {
        amount: 4,
        life: 0.6,
        duration: f32::INFINITY,
        radius: 0.008,
        spread: 45.,
        speed: Vec2::new(0.3, 0.7),
        gravity: Vec3::new(0., -2.2, 0.),
        size: Vec2::splat(0.06 * 0.25),
        colors: vec![(0., Vec4::ONE), (1., Vec4::ONE)],
        reference: Some(frame),
        material: Some(material),
        ..default()
    }
}
fn nozzle_position() -> Vec3 {
    std::env::var("UG_TORCHPOS")
        .ok()
        .and_then(|s| vector(&s).ok())
        .unwrap_or(Vec3::new(-0.039, 0.406, -0.026))
}
fn burst(clock: &mut f32, cutting: bool, dt: f32) -> bool {
    if !cutting {
        *clock = 0.;
        return false;
    }
    *clock -= dt;
    if *clock <= 0. {
        *clock = 0.1;
        true
    } else {
        false
    }
}
#[allow(clippy::too_many_arguments)]
fn feedback(
    mut commands: Commands,
    state: Res<ViewmodelState>,
    status: Res<ViewmodelStatus>,
    mut fx: ResMut<Feedback>,
    time: Res<Time>,
    ui: Res<UiState>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    settings: Res<Settings>,
    particle_assets: Option<Res<ParticleAssets>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut emitters: Query<&mut Emitter>,
    bodies: Query<(Entity, &Transform), With<crate::player::PlayerBody>>,
    rigs: Query<&RigPlayback>,
) {
    let paused = crate::player::simulation_paused(&ui);
    let dt = if paused { 0. } else { time.delta_secs() };
    let torch = status.tool == Kind::Torch && status.tool_active;
    let sound = if status.tool == Kind::Saw {
        Kind::Saw
    } else if torch
        && state
            .arms
            .and_then(|r| rigs.get(r).ok())
            .is_some_and(|r| !own_clip(r, &state.asset, "_Start_Swing", "").is_empty())
    {
        Kind::Torch
    } else {
        Kind::None
    };
    if fx.voice.is_some_and(|(kind, _)| kind != sound) {
        if let Some((_, entity)) = fx.voice.take() {
            commands.entity(entity).try_despawn();
        }
    }
    if sound != Kind::None && fx.voice.is_none() {
        let (path, spatial, unit, gain) = if sound == Kind::Torch {
            ("blowtorch_use.wav", false, 1., db(-5.))
        } else {
            let path = if config
                .content_root
                .join("audio/items/melee_chainsaw_idle.wav")
                .is_file()
            {
                "audio/items/melee_chainsaw_idle.wav"
            } else {
                "audio/items/melee_chainsaw_use.wav"
            };
            (
                path,
                true,
                5.,
                db(if status.tool_active { -20. } else { -14. }),
            )
        };
        if config.content_root.join(path).is_file() {
            let entity = commands
                .spawn((
                    Name::new(if sound == Kind::Torch {
                        "Blowtorch use loop"
                    } else {
                        "Chainsaw idle loop"
                    }),
                    ToolLoop(sound),
                    AudioPlayer::new(assets.load(path)),
                    PlaybackSettings {
                        paused,
                        ..PlaybackSettings::LOOP
                    }
                    .with_spatial(spatial)
                    .with_spatial_scale(SpatialScale::new(1. / unit))
                    .with_volume(Volume::Linear(gain * settings.master_volume)),
                    Transform::from_xyz(0., 1.2, 0.),
                ))
                .id();
            if spatial {
                if let Ok((body, _)) = bodies.single() {
                    commands.entity(entity).insert(ChildOf(body));
                }
            }
            fx.voice = Some((sound, entity));
        }
    }
    if !paused
        && burst(
            &mut fx.saw_clock,
            status.tool == Kind::Saw && status.tool_active,
            dt,
        )
    {
        if let Ok((_, body)) = bodies.single() {
            let path = "audio/items/melee_chainsaw_use.wav";
            if config.content_root.join(path).is_file() {
                let entity = crate::vehicle::spawn_ranged_audio(
                    &mut commands,
                    &assets,
                    None,
                    path,
                    false,
                    db(-6.),
                    1.,
                    6.,
                    40.,
                    settings.master_volume,
                );
                commands.entity(entity).insert(Transform::from_translation(
                    body.translation + Vec3::Y * 1.2,
                ));
            }
        }
    }
    let target = torch.then_some(state.gun.zip(state.frame)).flatten();
    if fx.sparks.is_some_and(|(gun, emitter)| {
        target.is_none_or(|(g, _)| gun != g) || emitters.get(emitter).is_err()
    }) {
        if let Some((_, emitter)) = fx.sparks.take() {
            if let Ok(mut e) = emitters.get_mut(emitter) {
                e.stop();
            }
        }
    }
    if let (Some((gun, frame)), Some(particle_assets)) = (target, particle_assets) {
        if fx.sparks.is_none() {
            let material = if let Some(material) = &fx.material {
                material.clone()
            } else {
                let texture = assets
                    .load_builder()
                    .with_settings(|settings: &mut bevy::image::ImageLoaderSettings| {
                        settings.sampler = bevy::image::ImageSampler::linear();
                    })
                    .load("torch_spark.png");
                let material = materials.add(StandardMaterial {
                    base_color: Color::linear_rgba(2.6, 2.6, 2.6, 1.),
                    base_color_texture: Some(texture),
                    unlit: true,
                    alpha_mode: AlphaMode::Blend,
                    cull_mode: None,
                    double_sided: true,
                    ..default()
                });
                fx.material = Some(material.clone());
                material
            };
            fx.seed = fx.seed.wrapping_add(1);
            let entity = particles::spawn(
                &mut commands,
                &mut meshes,
                &particle_assets,
                spark_spec(frame, material),
                nozzle_position(),
                Some(gun),
                fx.seed,
            );
            commands.entity(entity).insert((
                Nozzle { gun, frame },
                RenderLayers::layer(LAYER),
                Name::new("Original blowtorch blue sparks"),
            ));
            fx.sparks = Some((gun, entity));
        }
    }
}
fn aim_sparks(mut sparks: Query<(&Nozzle, &mut Emitter)>, poses: Query<&GlobalTransform>) {
    for (nozzle, mut emitter) in &mut sparks {
        if let (Ok(gun), Ok(frame)) = (poses.get(nozzle.gun), poses.get(nozzle.frame)) {
            emitter.set_direction(frame.rotation().inverse() * gun.rotation() * Vec3::Y);
        }
    }
}
fn db(db: f32) -> f32 {
    10_f32.powf(db / 20.)
}
fn update_loops(
    status: Res<ViewmodelStatus>,
    settings: Res<Settings>,
    ui: Res<UiState>,
    listeners: Query<&GlobalTransform, With<SpatialListener>>,
    mut loops: Query<(
        &ToolLoop,
        &GlobalTransform,
        &mut PlaybackSettings,
        Option<&mut AudioSink>,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = crate::player::simulation_paused(&ui);
    let listener = listeners.iter().next().map(GlobalTransform::translation);
    for (tool, pose, mut playback, sink, spatial) in &mut loops {
        let gain = db(if tool.0 == Kind::Torch {
            -5.
        } else if status.tool_active {
            -20.
        } else {
            -14.
        });
        let in_range =
            tool.0 != Kind::Saw || listener.is_none_or(|p| p.distance(pose.translation()) < 35.);
        let volume = Volume::Linear(if in_range {
            gain * settings.master_volume
        } else {
            0.
        });
        playback.volume = volume;
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
            if paused { sink.pause() } else { sink.play() }
        }
        if let Some(mut sink) = spatial {
            sink.set_volume(volume);
            if paused { sink.pause() } else { sink.play() }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn app(asset: &str, repair: bool) -> App {
        let mut app = App::new();
        let mut catalog = ItemCatalog::default();
        catalog.items.insert(
            76,
            serde_json::from_value(serde_json::json!({
                "id":76,"name":"test tool","type":"melee","asset":asset,
                "mesh":format!("{asset}.txt"),"texture":format!("{asset}_albedo.png"),
                "melee":{"repeated":true,"repair":repair}
            }))
            .unwrap(),
        );
        app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
            .init_asset::<Mesh>()
            .init_asset::<Image>()
            .init_asset::<StandardMaterial>()
            .init_asset::<SkinnedMeshInverseBindposes>()
            .init_asset::<AudioSource>()
            .init_resource::<WorldConfig>()
            .init_resource::<crate::world::WorldGeometry>()
            .init_resource::<crate::impact::surfaces::SurfaceTable>()
            .init_resource::<crate::player::WorldSessionEpoch>()
            .init_resource::<ContentCache>()
            .insert_resource(catalog)
            .init_resource::<Settings>()
            .init_resource::<crate::use_items::HandUse>()
            .init_resource::<crate::vehicle::Occupancy>()
            .init_resource::<crate::recoil::Recoil>()
            .init_resource::<ButtonInput<KeyCode>>()
            .init_resource::<ButtonInput<MouseButton>>()
            .add_message::<CoreEvent>()
            .add_message::<crate::use_items::HandEvent>()
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
                std::time::Duration::from_millis(20),
            ))
            .insert_resource(GameSnapshot(
                serde_json::json!({"connected":true,"playerId":1,
                "player":{"alive":true,"heldItemId":76,"stance":"stand"}}),
            ))
            .add_plugins((
                crate::rig::RigPlugin,
                crate::particles::ParticlePlugin,
                ViewmodelPlugin,
            ));
        app.world_mut().spawn((
            PlayerCamera,
            Camera::default(),
            Projection::default(),
            Transform::IDENTITY,
        ));
        for _ in 0..130 {
            app.update();
        }
        app
    }
    #[test]
    fn real_tool_rig_holds_start_through_hits_then_plays_source_stop() {
        for (asset, repair) in [("blowtorch", true), ("chainsaw", false)] {
            let mut app = app(asset, repair);
            assert!(app.world().resource::<ViewmodelStatus>().ready);
            app.world_mut()
                .resource_mut::<ButtonInput<MouseButton>>()
                .press(MouseButton::Left);
            app.update();
            let root = app.world().resource::<ViewmodelState>().arms.unwrap();
            let cap = asset[..1].to_uppercase() + &asset[1..];
            let rig = app.world().get::<RigPlayback>(root).unwrap();
            assert_eq!(rig.clip, format!("{cap}_Start_Swing"));
            assert!(!rig.looping);
            for sequence in 0..60 {
                app.world_mut().write_message(CoreEvent {
                    sequence,
                    kind: "melee".into(),
                    data: serde_json::json!({"playerId":1,"strong":false}),
                });
                app.update();
            }
            let rig = app.world().get::<RigPlayback>(root).unwrap();
            assert_eq!(rig.clip, format!("{cap}_Start_Swing"));
            assert!(rig.time >= rig.data.anims[&rig.clip].length);
            if repair {
                let emitter = app.world().resource::<Feedback>().sparks.unwrap().1;
                assert!(app.world().get::<Emitter>(emitter).unwrap().debug_state().1 > 0);
                assert_eq!(
                    app.world().get::<RenderLayers>(emitter).unwrap(),
                    &RenderLayers::layer(1)
                );
            }
            app.world_mut()
                .resource_mut::<ButtonInput<MouseButton>>()
                .release(MouseButton::Left);
            app.update();
            let rig = app.world().get::<RigPlayback>(root).unwrap();
            assert_eq!(
                rig.clip,
                own_clip(
                    rig,
                    asset,
                    "_Stop_Swing",
                    &own_clip(rig, asset, "_Equip", "Melee_Equip")
                )
            );
            assert!(!app.world().resource::<ViewmodelStatus>().tool_active);
            assert!(app.world().resource::<Feedback>().sparks.is_none());
            for _ in 0..40 {
                app.update();
            }
            assert_eq!(
                app.world_mut()
                    .query::<&Emitter>()
                    .iter(app.world())
                    .count(),
                0
            );
            if !repair {
                let voice = app.world().resource::<Feedback>().voice.unwrap().1;
                app.world_mut().resource_mut::<UiState>().screen = Screen::Paused;
                app.update();
                assert!(app.world().get::<PlaybackSettings>(voice).unwrap().paused);
                app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
                app.update();
                let playback = app.world().get::<PlaybackSettings>(voice).unwrap();
                assert!(!playback.paused);
                assert_eq!(playback.volume, Volume::Linear(db(-14.)));
            }
            // Unequipping also stops the continuous saw engine, while the torch
            // already stopped on release. No audio device is needed to test it.
            app.world_mut().resource_mut::<GameSnapshot>().0["player"]["heldItemId"] =
                serde_json::json!(0);
            app.update();
            assert!(app.world().resource::<Feedback>().voice.is_none());
        }
    }
    #[test]
    fn cut_sound_retriggers_on_press_without_catching_up_stalled_frames() {
        let mut clock = 0.;
        assert!(burst(&mut clock, true, 0.02));
        assert!(!burst(&mut clock, true, 0.02));
        assert!(!burst(&mut clock, false, 0.02));
        assert!(burst(&mut clock, true, 0.02));
        assert!(burst(&mut clock, true, 1.));
        assert!(!burst(&mut clock, true, 0.02));
    }
    #[test]
    fn source_tool_clips_exist_and_start_swing_holds_instead_of_restarting() {
        let data = std::sync::Arc::new(
            crate::rig::RigData::load(&WorldConfig::default().content_root.join("rig.json"))
                .unwrap(),
        );
        for asset in ["blowtorch", "chainsaw"] {
            let start = format!("{}_Start_Swing", asset[..1].to_uppercase() + &asset[1..]);
            assert!(data.anims[&start].length > 0.);
        }
        assert!(
            WorldConfig::default()
                .content_root
                .join("blowtorch_use.wav")
                .is_file()
        );
        assert!(
            WorldConfig::default()
                .content_root
                .join("audio/items/melee_chainsaw_idle.wav")
                .is_file()
        );
    }
}
