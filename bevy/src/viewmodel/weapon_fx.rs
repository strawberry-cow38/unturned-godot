//! Original Viewmodel.Kick, casing integration and gun/melee sound scheduling.
//! Events are accepted authority facts; these visuals never create damage.
use super::*;
use crate::{
    impact::surfaces::{Surface, Surfaces},
    player::{PlayerBody, WorldSessionEpoch},
};
use bevy::{
    audio::{AudioSinkPlayback, Volume},
    image::{ImageLoaderSettings, ImageSampler},
};
use bevy_rapier3d::prelude::*;
use serde_json::Value;

const EJECT: Vec3 = Vec3::new(0., 0.0275, -0.0814);
const FLASH_SECONDS: f32 = 0.05;
const CASING_SECONDS: f32 = 1.3;

pub(super) struct WeaponEffectsPlugin;
impl Plugin for WeaponEffectsPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Feedback>()
            .init_resource::<Diagnostics>()
            .add_systems(
                Update,
                (decay, receive, audio_control)
                    .chain()
                    .after(animate_viewmodel),
            )
            .add_systems(
                PostUpdate,
                (billboard, spawn_visuals)
                    .chain()
                    .after(bevy::transform::TransformSystems::Propagate)
                    .before(bevy::camera::visibility::VisibilitySystems::VisibilityPropagate)
                    .before(bevy::camera::visibility::VisibilitySystems::CalculateBounds),
            );
    }
}
#[derive(Resource)]
struct Diagnostics {
    hold_flash: bool,
}
impl Default for Diagnostics {
    fn default() -> Self {
        Self {
            hold_flash: std::env::var("UG_FLASHHOLD").as_deref() == Ok("1"),
        }
    }
}
#[derive(Resource, Default)]
struct Feedback {
    epoch: u64,
    last_event: u64,
    owner: Option<Entity>,
    roll: f32,
    seed: u64,
    pending: Vec<Shot>,
    hammer: Option<Hammer>,
    swing: Option<Entity>,
    flash: Option<Entity>,
    geometry: Option<Geometry>,
    banks: HashMap<&'static str, crate::sound::Bank>,
    surface: &'static str,
    surface_timer: f32,
}
struct Shot {
    gun: Entity,
    camera: Entity,
    frame: Entity,
    muzzle: Vec3,
    ejects: bool,
    silenced: bool,
}
struct Hammer {
    owner: Entity,
    delay: f32,
    speed: f32,
    asset: String,
}
struct Geometry {
    flash: Handle<Mesh>,
    flash_material: Handle<StandardMaterial>,
    casing: Handle<Mesh>,
    casing_material: Handle<StandardMaterial>,
}
#[derive(Component)]
struct Transient;
#[derive(Component)]
struct Flash {
    age: f32,
    camera: Entity,
    roll: f32,
}
#[derive(Component)]
struct Casing {
    owner: Entity,
    age: f32,
    velocity: Vec3,
    spin: Vec3,
    bounced: bool,
}
#[derive(Component)]
struct Voice {
    gain: f32,
}
fn db(value: f32) -> f32 {
    10_f32.powf(value / 20.)
}
fn number(value: &Value, fallback: f32) -> f32 {
    value
        .as_f64()
        .filter(|v| v.is_finite())
        .map_or(fallback, |v| v as f32)
}
pub(super) fn hammer_timing(rig: &RigPlayback, asset: &str, data: &Value) -> Option<(f32, f32)> {
    let key = own_clip(rig, asset, "_Hammer", "");
    let length = rig.data.anims.get(&key)?.length;
    Some((
        number(&data["delay"], 0.).max(0.),
        length / number(&data["seconds"], length).max(0.01),
    ))
}
fn clip(root: &Path, asset: &str, kind: &str) -> Option<String> {
    let own = format!("{asset}_{kind}.ogg");
    if root.join(&own).is_file() {
        return Some(own);
    }
    if kind == "dryfire" {
        return None;
    }
    let fallback = format!("eaglefire_{kind}.ogg");
    root.join(&fallback).is_file().then_some(fallback)
}
fn spawn_voice(
    commands: &mut Commands,
    assets: &AssetServer,
    settings: &Settings,
    owner: Option<Entity>,
    path: String,
    gain: f32,
    speed: f32,
    paused: bool,
) -> Entity {
    let e = commands
        .spawn((
            Name::new(format!("Weapon audio {path}")),
            Transient,
            Voice { gain },
            AudioPlayer::new(assets.load(path)),
            PlaybackSettings {
                paused,
                ..PlaybackSettings::DESPAWN
            }
            .with_volume(Volume::Linear(gain * settings.master_volume))
            .with_speed(speed.max(0.01)),
        ))
        .id();
    if let Some(owner) = owner {
        commands.entity(e).insert(ChildOf(owner));
    }
    e
}
#[allow(clippy::too_many_arguments)]
fn receive(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    state: Res<ViewmodelState>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    settings: Res<Settings>,
    ui: Res<UiState>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time>,
    mut fx: ResMut<Feedback>,
    transient: Query<Entity, With<Transient>>,
    rigs: Query<&RigPlayback>,
) {
    let active = snapshot.0["connected"].as_bool().unwrap_or(false)
        && snapshot.0["player"]["alive"].as_bool().unwrap_or(true);
    if fx.epoch != epoch.0 || !active {
        for entity in &transient {
            commands.entity(entity).try_despawn();
        }
        fx.pending.clear();
        fx.hammer = None;
        fx.swing = None;
        fx.flash = None;
        fx.last_event = 0;
        fx.epoch = epoch.0;
        fx.owner = None;
    }
    if fx.owner != state.gun {
        fx.hammer = None;
        fx.roll = 0.;
        fx.flash = None;
        fx.owner = state.gun;
    }
    let paused = crate::player::simulation_paused(&ui);
    let dt = if paused { 0. } else { time.delta_secs() };
    if let Some(mut hammer) = fx.hammer.take() {
        hammer.delay -= dt;
        if hammer.delay <= 0. && active {
            if let Some(path) = clip(&config.content_root, &hammer.asset, "hammer") {
                spawn_voice(
                    &mut commands,
                    &assets,
                    &settings,
                    Some(hammer.owner),
                    path,
                    db(-3.),
                    hammer.speed,
                    paused,
                );
            }
        } else {
            fx.hammer = Some(hammer);
        }
    }
    for event in events.read() {
        if event.sequence <= fx.last_event {
            continue;
        }
        fx.last_event = event.sequence;
        if !active || event.data["playerId"] != snapshot.0["playerId"] {
            continue;
        }
        match event.kind.as_str() {
            "fire" => {
                let asset = event.data["gun"].as_str().unwrap_or(&state.asset);
                let path = event.data["shootClip"]
                    .as_str()
                    .filter(|path| config.content_root.join(path).is_file())
                    .map(str::to_owned)
                    .or_else(|| clip(&config.content_root, asset, "shoot"));
                if let Some(path) = path {
                    spawn_voice(
                        &mut commands,
                        &assets,
                        &settings,
                        (asset == state.asset).then_some(state.gun).flatten(),
                        path,
                        db(-3.) * number(&event.data["shotVolume"], 1.).max(0.0001),
                        1.,
                        paused,
                    );
                }
                if asset == state.asset && state.is_gun && !state.seated {
                    if let (Some(gun), Some(camera), Some(visual)) =
                        (state.gun, state.camera, state.visuals.get(asset))
                    {
                        fx.pending.push(Shot {
                            gun,
                            camera,
                            frame: state.frame.unwrap_or(camera),
                            muzzle: visual.muzzle,
                            ejects: visual.ejects,
                            // Original muzzle light uses the installed barrel;
                            // integral suppression additionally hides tracers.
                            silenced: event.data["barrelSilenced"]
                                .as_bool()
                                .or_else(|| event.data["silenced"].as_bool())
                                .unwrap_or(false),
                        });
                    }
                }
            }
            "reload" => {
                if let Some(owner) = state.gun {
                    if let Some(path) = clip(&config.content_root, &state.asset, "reload") {
                        spawn_voice(
                            &mut commands,
                            &assets,
                            &settings,
                            Some(owner),
                            path,
                            db(-3.),
                            number(&event.data["speed"], 1.),
                            paused,
                        );
                    }
                }
            }
            "rechamber" => {
                if let Some(owner) = state.gun {
                    let timing = state
                        .arms
                        .and_then(|arms| rigs.get(arms).ok())
                        .and_then(|rig| hammer_timing(rig, &state.asset, &event.data));
                    if let Some((delay, speed)) = timing {
                        let delay = (delay - dt).max(0.);
                        fx.hammer = Some(Hammer {
                            owner,
                            delay,
                            speed,
                            asset: state.asset.clone(),
                        });
                        if delay == 0. {
                            let hammer = fx.hammer.take().unwrap();
                            if let Some(path) = clip(&config.content_root, &hammer.asset, "hammer")
                            {
                                spawn_voice(
                                    &mut commands,
                                    &assets,
                                    &settings,
                                    Some(owner),
                                    path,
                                    db(-3.),
                                    hammer.speed,
                                    paused,
                                );
                            }
                        }
                    }
                }
            }
            "dryFire" => {
                if let Some(path) = clip(&config.content_root, &state.asset, "dryfire") {
                    spawn_voice(
                        &mut commands,
                        &assets,
                        &settings,
                        state.gun,
                        path,
                        db(-3.),
                        1.,
                        paused,
                    );
                }
            }
            "melee" => {
                let id = event.data["itemId"]
                    .as_u64()
                    .map(|id| id as u16)
                    .or(state.held)
                    .unwrap_or(0);
                if event.data["repeated"].as_bool().unwrap_or_else(|| {
                    catalog
                        .items
                        .get(&id)
                        .is_some_and(|item| item.melee["repeated"].as_bool() == Some(true))
                }) {
                    continue;
                }
                if let Some(previous) = fx.swing.take() {
                    commands.entity(previous).try_despawn();
                }
                let n = (next(&mut fx.seed) * 2.) as u8;
                let path = format!("melee_swing_{n}.wav");
                if config.content_root.join(&path).is_file() {
                    fx.swing = Some(spawn_voice(
                        &mut commands,
                        &assets,
                        &settings,
                        state.arms,
                        path,
                        db(-5.),
                        1.,
                        paused,
                    ));
                }
            }
            _ => {}
        }
    }
}
fn next(seed: &mut u64) -> f32 {
    *seed = (*seed).max(1);
    crate::particles::random(seed)
}
fn between(seed: &mut u64, min: f32, max: f32) -> f32 {
    min + (max - min) * next(seed)
}
fn casing_motion(seed: &mut u64) -> (Vec3, Vec3) {
    (
        Vec3::new(
            between(seed, 1.8, 2.4),
            between(seed, 1., 1.4),
            -between(seed, 0.3, 0.7),
        ),
        Vec3::new(
            between(seed, -18., 18.),
            between(seed, -18., 18.),
            between(seed, -18., 18.),
        ),
    )
}
fn geometry(
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    assets: &AssetServer,
) -> Geometry {
    Geometry {
        flash: meshes.add(Rectangle::new(0.6, 0.6)),
        flash_material: materials.add(StandardMaterial {
            base_color: Color::linear_rgba(2.6, 2.6, 2.6, 1.),
            base_color_texture: Some(
                assets
                    .load_builder()
                    .with_settings(|s: &mut ImageLoaderSettings| {
                        s.sampler = ImageSampler::linear();
                    })
                    .load("muzzleflash.png"),
            ),
            unlit: true,
            cull_mode: None,
            alpha_mode: AlphaMode::Add,
            ..default()
        }),
        casing: meshes.add(Cuboid::new(0.0135, 0.0135, 0.042)),
        casing_material: materials.add(StandardMaterial {
            base_color: Color::srgb(0.904, 0.768, 0.007),
            unlit: true,
            ..default()
        }),
    }
}
fn spawn_visuals(
    mut commands: Commands,
    mut fx: ResMut<Feedback>,
    cameras: Query<&GlobalTransform, With<ViewmodelCamera>>,
    frames: Query<&GlobalTransform, With<ViewmodelFrame>>,
    guns: Query<&GlobalTransform, With<HeldMesh>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    assets: Res<AssetServer>,
) {
    // Preload the flash sprite before the first short-lived flash needs it.
    if fx.geometry.is_none() {
        fx.geometry = Some(geometry(&mut meshes, &mut materials, &assets));
    }
    let pending = std::mem::take(&mut fx.pending);
    for shot in pending {
        let (Ok(gun), Ok(camera)) = (guns.get(shot.gun), cameras.get(shot.camera)) else {
            continue;
        };
        let camera_rotation = camera.rotation();
        let sign = if next(&mut fx.seed) < 0.5 { -1. } else { 1. };
        fx.roll += sign * between(&mut fx.seed, 0.35, 1.);
        if let Some(old) = fx.flash.take() {
            commands.entity(old).try_despawn();
        }
        if !shot.silenced {
            let global = Transform::from_translation(gun.transform_point(shot.muzzle))
                .with_rotation(camera_rotation * Quat::from_rotation_z(-fx.roll));
            let local = Transform::from_matrix(gun.to_matrix().inverse() * global.to_matrix());
            let geometry = fx.geometry.as_ref().unwrap();
            let flash = commands
                .spawn((
                    Name::new("Original muzzle flash"),
                    Transient,
                    Flash {
                        age: 0.,
                        camera: shot.camera,
                        roll: fx.roll,
                    },
                    Mesh3d(geometry.flash.clone()),
                    MeshMaterial3d(geometry.flash_material.clone()),
                    local,
                    GlobalTransform::from(global),
                    RenderLayers::layer(LAYER),
                    ChildOf(shot.gun),
                ))
                .id();
            commands.spawn((
                Name::new("Muzzle flash light"),
                PointLight {
                    color: Color::srgb(0.941, 0.756, 0.152),
                    range: 4.,
                    intensity: 1.4 * crate::projectiles::effects::LIGHT_UNIT,
                    shadow_maps_enabled: false,
                    ..default()
                },
                Transform::IDENTITY,
                GlobalTransform::from(global),
                RenderLayers::layer(LAYER),
                ChildOf(flash),
            ));
            fx.flash = Some(flash);
        }
        if shot.ejects {
            let frame = frames.get(shot.frame).unwrap_or(camera);
            let global = Transform::from_translation(gun.transform_point(EJECT))
                .with_rotation(gun.rotation());
            let local = Transform::from_matrix(frame.to_matrix().inverse() * global.to_matrix());
            let (velocity, spin) = casing_motion(&mut fx.seed);
            let velocity = frame.rotation().inverse() * camera.rotation() * velocity;
            let geometry = fx.geometry.as_ref().unwrap();
            commands.spawn((
                Name::new("Original ejected casing"),
                Transient,
                Casing {
                    owner: shot.gun,
                    age: 0.,
                    velocity,
                    spin,
                    bounced: false,
                },
                Mesh3d(geometry.casing.clone()),
                MeshMaterial3d(geometry.casing_material.clone()),
                local,
                GlobalTransform::from(global),
                RenderLayers::layer(LAYER),
                ChildOf(shot.frame),
            ));
        }
    }
}
fn billboard(
    mut flashes: Query<(&Flash, &ChildOf, &mut Transform, &mut GlobalTransform)>,
    frames: Query<&GlobalTransform, Without<Flash>>,
) {
    for (flash, parent, mut local, mut global) in &mut flashes {
        let (Ok(gun), Ok(camera)) = (frames.get(parent.parent()), frames.get(flash.camera)) else {
            continue;
        };
        let position = gun.transform_point(local.translation);
        let world = Transform::from_translation(position)
            .with_rotation(camera.rotation() * Quat::from_rotation_z(-flash.roll));
        *local = Transform::from_matrix(gun.to_matrix().inverse() * world.to_matrix());
        *global = GlobalTransform::from(world);
    }
}
fn casing_bank(surface: Surface) -> &'static str {
    match surface {
        Surface::Metal => "metal",
        Surface::Wood => "wood",
        Surface::Sand => "sand",
        Surface::Water => "water",
        _ => "general",
    }
}
#[allow(clippy::too_many_arguments)]
fn decay(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    mut fx: ResMut<Feedback>,
    mut flashes: Query<(Entity, &mut Flash)>,
    mut casings: Query<(Entity, &mut Casing, &mut Transform)>,
    guns: Query<(), With<HeldMesh>>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
    bodies: Query<(Entity, &Transform, &PlayerBody), Without<Casing>>,
    occupancy: Res<crate::vehicle::Occupancy>,
    rapier: ReadRapierContext,
    surfaces: Surfaces,
    diagnostic: Res<Diagnostics>,
) {
    let paused = crate::player::simulation_paused(&ui);
    let dt = if paused { 0. } else { time.delta_secs() };
    for (entity, mut flash) in &mut flashes {
        if !diagnostic.hold_flash {
            flash.age += dt;
        }
        if flash.age >= FLASH_SECONDS {
            commands.entity(entity).try_despawn();
        }
    }
    fx.surface_timer -= dt;
    if fx.surface_timer <= 0. {
        fx.surface_timer = 0.2;
        if occupancy.seat.is_some() {
            fx.surface = "metal";
        } else if let Ok((body, pose, state)) = bodies.single() {
            if state.stance() == "swim" {
                fx.surface = "water";
            } else if state.grounded() {
                if let Some((entity, hit)) = rapier.single().ok().and_then(|r| {
                    r.cast_ray_and_get_normal(
                        pose.translation + Vec3::Y * 0.15,
                        Vec3::NEG_Y,
                        0.6,
                        true,
                        QueryFilter::default()
                            .exclude_collider(body)
                            .exclude_sensors(),
                    )
                }) {
                    fx.surface = casing_bank(surfaces.at(entity, hit.point).0);
                }
            }
        }
    }
    for (entity, mut casing, mut pose) in &mut casings {
        if guns.get(casing.owner).is_err() {
            commands.entity(entity).try_despawn();
            continue;
        }
        casing.age += dt;
        casing.velocity.y -= 9.8 * dt;
        pose.translation += casing.velocity * dt;
        pose.rotate_x(casing.spin.x * dt);
        pose.rotate_y(casing.spin.y * dt);
        pose.rotate_z(casing.spin.z * dt);
        if !casing.bounced && casing.age > 0.5 {
            casing.bounced = true;
            let surface = if fx.surface.is_empty() {
                "general"
            } else {
                fx.surface
            };
            let seed = (next(&mut fx.seed) * u32::MAX as f32) as u64;
            let pitch = between(&mut fx.seed, 0.92, 1.08);
            let path = fx
                .banks
                .entry(surface)
                .or_insert_with(|| {
                    crate::sound::Bank::load(&config.content_root, "casings", surface)
                })
                .pick(seed)
                .map(str::to_owned);
            if let Some(path) = path {
                spawn_voice(
                    &mut commands,
                    &assets,
                    &settings,
                    None,
                    path,
                    db(-12.),
                    pitch,
                    paused,
                );
            }
        }
        if casing.age > CASING_SECONDS {
            commands.entity(entity).try_despawn();
        }
    }
}
fn audio_control(
    ui: Res<UiState>,
    settings: Res<Settings>,
    mut voices: Query<(&Voice, &mut PlaybackSettings, Option<&mut AudioSink>)>,
) {
    let paused = crate::player::simulation_paused(&ui);
    for (voice, mut playback, sink) in &mut voices {
        let volume = Volume::Linear(voice.gain * settings.master_volume);
        playback.volume = volume;
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
            if paused {
                sink.pause();
            } else {
                sink.play();
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use bevy::audio::Decodable;
    use serde_json::json;
    fn app() -> App {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
            .init_asset::<Mesh>()
            .init_asset::<StandardMaterial>()
            .init_asset::<Image>()
            .init_asset::<AudioSource>()
            .init_resource::<WorldConfig>()
            .init_resource::<crate::world::WorldGeometry>()
            .init_resource::<crate::impact::surfaces::SurfaceTable>()
            .init_resource::<WorldSessionEpoch>()
            .init_resource::<Settings>()
            .init_resource::<crate::vehicle::Occupancy>()
            .init_resource::<ItemCatalog>()
            .insert_resource(UiState {
                screen: Screen::Playing,
                ..default()
            })
            .insert_resource(GameSnapshot(
                json!({"connected":true,"playerId":1,"player":{"alive":true,"heldItemId":4}}),
            ))
            .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
                std::time::Duration::from_millis(20),
            ))
            .add_message::<CoreEvent>();
        let frame = app
            .world_mut()
            .spawn((
                ViewmodelFrame,
                Transform::from_xyz(100., 2., 300.).with_rotation(Quat::from_rotation_y(0.4)),
            ))
            .id();
        let camera = app
            .world_mut()
            .spawn((ViewmodelCamera, Transform::IDENTITY, ChildOf(frame)))
            .id();
        let gun = app
            .world_mut()
            .spawn((
                HeldMesh,
                Transform::from_xyz(0.2, -0.3, -0.5).with_rotation(Quat::from_euler(
                    EulerRot::ZYX,
                    0.2,
                    0.3,
                    0.4,
                )),
                ChildOf(frame),
            ))
            .id();
        let visuals =
            load_gun_visuals(&app.world().resource::<WorldConfig>().content_root).unwrap();
        app.insert_resource(ViewmodelState {
            camera: Some(camera),
            frame: Some(frame),
            gun: Some(gun),
            held: Some(4),
            asset: "eaglefire".into(),
            is_gun: true,
            visuals,
            ..default()
        })
        .add_plugins(WeaponEffectsPlugin);
        app.update();
        app
    }
    fn send(app: &mut App, sequence: u64, kind: &str, data: Value) {
        app.world_mut().write_message(CoreEvent {
            sequence,
            kind: kind.into(),
            data,
        });
    }
    fn shot(silenced: bool) -> Value {
        json!({"playerId":1,"gun":"eaglefire","itemId":4,"silenced":silenced,"barrelSilenced":silenced,
            "shootClip":"eaglefire_shoot.ogg","shotVolume":if silenced {0.6} else {1.}})
    }
    fn count<T: Component>(app: &mut App) -> usize {
        app.world_mut().query::<&T>().iter(app.world()).count()
    }
    #[test]
    fn accepted_shots_overlap_audio_eject_once_and_keep_bottle_suppression_after_break() {
        let mut app = app();
        send(&mut app, 1, "fire", shot(true));
        send(&mut app, 2, "fire", shot(true));
        app.update();
        assert_eq!(count::<Voice>(&mut app), 2, "each shot needs its own voice");
        assert_eq!(count::<Casing>(&mut app), 2);
        assert_eq!(count::<Flash>(&mut app), 0);
        for voice in app.world_mut().query::<&Voice>().iter(app.world()) {
            assert!((voice.gain - db(-3.) * 0.6).abs() < 0.00001);
        }
        send(&mut app, 2, "fire", shot(true));
        app.update();
        assert_eq!(count::<Voice>(&mut app), 2);
        assert_eq!(
            count::<Casing>(&mut app),
            2,
            "retained fact replay must not eject twice"
        );
        // The gun is already unsilenced in the new snapshot; the two accepted
        // facts above still own their suppressed first-shot presentation.
        app.world_mut().resource_mut::<GameSnapshot>().0["weapon"] = json!({"silenced":false});
        send(&mut app, 3, "fire", shot(false));
        app.update();
        assert_eq!(count::<Voice>(&mut app), 3);
        assert_eq!(count::<Flash>(&mut app), 1);
        let camera = app.world().resource::<ViewmodelState>().camera.unwrap();
        let gun = app.world().resource::<ViewmodelState>().gun.unwrap();
        app.world_mut()
            .get_mut::<Transform>(camera)
            .unwrap()
            .rotation = Quat::from_rotation_y(1.1);
        app.world_mut().get_mut::<Transform>(gun).unwrap().rotation = Quat::from_rotation_x(0.6);
        app.update();
        let rotation = app
            .world()
            .get::<GlobalTransform>(camera)
            .unwrap()
            .rotation();
        let mut query = app.world_mut().query::<(&Flash, &GlobalTransform)>();
        let (flash, global) = query.single(app.world()).unwrap();
        let expected = rotation * Quat::from_rotation_z(-flash.roll);
        for axis in [Vec3::X, Vec3::Y, Vec3::Z] {
            assert!(
                (global.rotation() * axis - expected * axis).length() < 0.00001,
                "flash must billboard after gun and camera rotate: {:?} vs {:?}",
                global.rotation(),
                expected
            );
        }
        app.update();
        app.update();
        assert_eq!(count::<Flash>(&mut app), 0);
    }
    #[test]
    fn camera_relative_casings_tumble_bounce_once_and_cleanup_after_unequip_or_epoch() {
        let mut app = app();
        send(&mut app, 1, "fire", shot(false));
        app.update();
        let camera = app.world().resource::<ViewmodelState>().camera.unwrap();
        let frame = app.world().resource::<ViewmodelState>().frame.unwrap();
        let gun = app.world().resource::<ViewmodelState>().gun.unwrap();
        let (casing_entity, initial) = app
            .world_mut()
            .query::<(Entity, &Casing, &Transform)>()
            .iter(app.world())
            .map(|(e, _, t)| (e, *t))
            .next()
            .unwrap();
        let initial_velocity = app.world().get::<Casing>(casing_entity).unwrap().velocity;
        let spin = app.world().get::<Casing>(casing_entity).unwrap().spin;
        assert!((1.8..=2.4).contains(&initial_velocity.x));
        assert!((1.0..=1.4).contains(&initial_velocity.y));
        app.world_mut()
            .get_mut::<Transform>(frame)
            .unwrap()
            .translation += Vec3::new(256., 3., -512.);
        app.world_mut()
            .get_mut::<Transform>(camera)
            .unwrap()
            .rotation = Quat::from_rotation_y(1.1);
        app.update();
        let current = *app.world().get::<Transform>(casing_entity).unwrap();
        assert!(
            (current.translation
                - initial.translation
                - (initial_velocity - Vec3::Y * 9.8 * 0.02) * 0.02)
                .length()
                < 0.00001
        );
        assert_eq!(
            app.world().get::<ChildOf>(casing_entity).unwrap().parent(),
            frame,
            "Alt-look rotates only the camera, leaving previous casings in the source viewport frame"
        );
        let expected_rotation = Quat::from_rotation_z(spin.z * 0.02)
            * Quat::from_rotation_y(spin.y * 0.02)
            * Quat::from_rotation_x(spin.x * 0.02)
            * initial.rotation;
        for axis in [Vec3::X, Vec3::Y, Vec3::Z] {
            assert!(
                (current.rotation * axis - expected_rotation * axis).length() < 0.00001,
                "source casing rotations compose about the viewport parent axes"
            );
        }
        for _ in 0..25 {
            app.update();
        }
        assert!(app.world().get::<Casing>(casing_entity).unwrap().bounced);
        assert_eq!(
            count::<Voice>(&mut app),
            2,
            "one shot plus one casing bounce"
        );
        for _ in 0..10 {
            app.update();
        }
        assert_eq!(count::<Voice>(&mut app), 2);
        app.world_mut().despawn(gun);
        app.world_mut().resource_mut::<ViewmodelState>().gun = None;
        app.update();
        assert_eq!(count::<Casing>(&mut app), 0);
        app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
        app.update();
        assert_eq!(
            count::<Transient>(&mut app),
            0,
            "reset must also clear detached bounce voices"
        );
    }
    #[test]
    fn accepted_integral_suppression_and_continuous_melee_do_not_invent_barrel_or_whoosh() {
        let mut app = app();
        let mut data = shot(true);
        data["barrelSilenced"] = json!(false);
        send(&mut app, 1, "fire", data);
        app.update();
        assert_eq!(
            count::<Flash>(&mut app),
            1,
            "source integral suppression and barrel flash gating are distinct"
        );
        send(
            &mut app,
            2,
            "melee",
            json!({"playerId":1,"itemId":490,"repeated":true}),
        );
        app.update();
        assert_eq!(
            count::<Voice>(&mut app),
            1,
            "old chainsaw hit must not whoosh after the held gun changes"
        );
        send(
            &mut app,
            3,
            "melee",
            json!({"playerId":1,"itemId":0,"repeated":false}),
        );
        app.update();
        assert_eq!(count::<Voice>(&mut app), 2);
        send(
            &mut app,
            4,
            "melee",
            json!({"playerId":1,"itemId":0,"repeated":false}),
        );
        app.update();
        assert_eq!(
            count::<Voice>(&mut app),
            2,
            "source melee is one restarted voice, unlike gunfire"
        );
    }
    #[test]
    fn reload_pitch_and_delayed_hammer_use_the_real_clip_and_cancel_on_switch() {
        let mut app = app();
        let data = std::sync::Arc::new(
            crate::rig::RigData::load(&WorldConfig::default().content_root.join("rig.json"))
                .unwrap(),
        );
        let length = data.anims["Eaglefire_Hammer"].length;
        let arms = app
            .world_mut()
            .spawn(RigPlayback {
                data,
                bones: default(),
                clip: "Eaglefire_Equip".into(),
                time: 0.,
                speed: 1.,
                looping: false,
                paused: false,
                overlay: None,
                additive: None,
            })
            .id();
        app.world_mut().resource_mut::<ViewmodelState>().arms = Some(arms);
        send(&mut app, 1, "reload", json!({"playerId":1,"speed":1.25}));
        app.update();
        let playback = app
            .world_mut()
            .query_filtered::<&PlaybackSettings, With<Voice>>()
            .single(app.world())
            .unwrap();
        assert_eq!(playback.speed, 1.25);
        send(
            &mut app,
            2,
            "rechamber",
            json!({"playerId":1,"delay":0.2,"seconds":length/1.5}),
        );
        app.update();
        assert_eq!(count::<Voice>(&mut app), 1);
        for _ in 0..8 {
            app.update();
        }
        assert_eq!(count::<Voice>(&mut app), 1);
        for _ in 0..2 {
            app.update();
        }
        assert_eq!(count::<Voice>(&mut app), 2);
        assert!(
            app.world_mut()
                .query_filtered::<&PlaybackSettings, With<Voice>>()
                .iter(app.world())
                .any(|p| (p.speed - 1.5).abs() < 0.00001)
        );
        send(
            &mut app,
            3,
            "rechamber",
            json!({"playerId":1,"delay":0.5,"seconds":length}),
        );
        app.update();
        app.world_mut().resource_mut::<ViewmodelState>().gun = None;
        for _ in 0..26 {
            app.update();
        }
        assert_eq!(
            count::<Voice>(&mut app),
            2,
            "cancel delayed hammer when selected gun changes"
        );
    }
    #[test]
    fn original_ogg_clips_decode_and_missing_dry_fire_stays_silent() {
        let root = WorldConfig::default().content_root;
        for kind in ["shoot", "reload", "hammer"] {
            let path = clip(&root, "eaglefire", kind).unwrap();
            let audio = AudioSource {
                bytes: std::fs::read(root.join(path)).unwrap().into(),
            };
            let samples: Vec<_> = audio.decoder().collect();
            assert!(
                samples.len() > 1000 && samples.iter().any(|v| v.abs() > 0.00001),
                "original {kind} must decode audible samples after its authored lead-in"
            );
        }
        assert_eq!(
            clip(&root, "missing-gun", "shoot").as_deref(),
            Some("eaglefire_shoot.ogg")
        );
        assert!(clip(&root, "missing-gun", "dryfire").is_none());
    }
    #[test]
    fn muzzle_lifetime_casings_and_voice_pause_preserve_state() {
        let mut app = app();
        send(&mut app, 1, "fire", shot(false));
        app.update();
        let before = app
            .world_mut()
            .query::<&Transform>()
            .iter(app.world())
            .count();
        app.world_mut().resource_mut::<UiState>().screen = Screen::Paused;
        for _ in 0..40 {
            app.update();
        }
        assert_eq!(count::<Flash>(&mut app), 1);
        assert_eq!(count::<Casing>(&mut app), 1);
        assert_eq!(
            app.world_mut()
                .query::<&Transform>()
                .iter(app.world())
                .count(),
            before
        );
        let playback = app
            .world_mut()
            .query_filtered::<&PlaybackSettings, With<Voice>>()
            .single(app.world())
            .unwrap();
        assert!(playback.paused);
        app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
        for _ in 0..70 {
            app.update();
        }
        assert_eq!(count::<Casing>(&mut app), 0);
        assert_eq!(count::<Flash>(&mut app), 0);
    }
}
