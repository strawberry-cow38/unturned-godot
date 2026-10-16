//! InteractableGenerator presentation from accepted state: the source
//! `_powerLevel` spin-up (1.3 s) / wind-down (1.1 s), the looping Engine voice
//! and the requested body shake. The level is presentation and the toggle gate's
//! anti-spam buffer only; fuel, output and toggles are the server's.
use super::*;
use crate::{player::PlayerCamera, settings::Settings, ui::UiState};
use bevy::audio::{AudioSinkPlayback, SpatialScale, Volume};
use presentation::BodyMesh;

const WARMUP: f32 = 1.3;
const COOLDOWN: f32 = 1.1;
/// AudioStreamPlayer3D UnitSize / MaxDistance on the source Engine voice.
const UNIT: f32 = 8.;
const RANGE: f32 = 45.;

/// `_powerLevel` per accepted generator.
#[derive(Resource, Default)]
pub(super) struct Ramps {
    pub levels: HashMap<u32, f32>,
    epoch: u64,
}
#[derive(Component)]
pub(super) struct Voice {
    net_id: u32,
    epoch: u64,
}
#[derive(Component, Default)]
pub(super) struct Shake {
    phase: f32,
}

pub(super) fn install(app: &mut App) {
    app.init_resource::<Ramps>().add_systems(
        Update,
        (
            ramp.after(super::sync),
            (voice, shake).after(ramp).after(wires::refresh),
        ),
    );
}

/// Source RunTarget: toggled on, not burning, with a tank and fuel in it.
pub(super) fn run_target(def: &Definition, row: &Value) -> f32 {
    if def.fuel > 0.
        && row["toggledOn"] == true
        && row["onFire"] != true
        && row["fuel"].as_f64().unwrap_or(0.) > 0.
    {
        1.
    } else {
        0.
    }
}
pub(super) fn ramp(
    time: Res<Time>,
    ui: Option<Res<UiState>>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    epoch: Res<WorldSessionEpoch>,
    mut ramps: ResMut<Ramps>,
) {
    if ramps.epoch != epoch.0 {
        *ramps = Ramps {
            epoch: epoch.0,
            ..default()
        };
    }
    // Singleplayer pause freezes the cosmetic clock with the rest of the scene.
    let dt = if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        0.
    } else {
        time.delta_secs()
    };
    let rows = snapshot.0["deployables"].as_array();
    let live: HashSet<u32> = rows
        .into_iter()
        .flatten()
        .filter_map(|r| r["netId"].as_u64().and_then(|id| u32::try_from(id).ok()))
        .collect();
    ramps.levels.retain(|id, _| live.contains(id));
    for row in rows.into_iter().flatten() {
        let Some(id) = row["netId"].as_u64().and_then(|id| u32::try_from(id).ok()) else {
            continue;
        };
        let target = catalog
            .deployables
            .iter()
            .find(|d| Some(d.id as u64) == row["defId"].as_u64())
            .map_or(0., |d| run_target(d, row));
        let level = ramps.levels.entry(id).or_default();
        // TakeDamage/BreakApart: a dying generator cuts out instantly, no wind-down.
        if row["onFire"] == true {
            *level = 0.;
            continue;
        }
        *level = if *level < target {
            (*level + dt / WARMUP).min(target)
        } else {
            (*level - dt / COOLDOWN).max(target)
        };
    }
}

/// `LoadFraction`: the accepted draw on the generator's (last) output over its rating.
pub(super) fn load_fraction(def: &Definition, accepted: &wires::Accepted, net_id: u32) -> f32 {
    let Some(index) = def.ports.iter().rposition(|p| p.kind == "Output") else {
        return 0.;
    };
    let watts = def.ports[index].watts;
    match accepted.port(net_id, index) {
        Some(port) if watts > 0. => (port.draw / watts).clamp(0., 1.),
        _ => 0.,
    }
}

/// Source `_Process` Engine block: play while `_powerLevel > 0.01`, spinning up
/// 0.6→1.0 in pitch and bogging down under load, louder as it works.
fn voice(
    mut commands: Commands,
    ramps: Res<Ramps>,
    accepted: Res<wires::Accepted>,
    catalog: Res<ItemCatalog>,
    config: Res<crate::world::WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
    settings: Option<Res<Settings>>,
    ui: Option<Res<UiState>>,
    bodies: Query<(&Deployed, &GlobalTransform)>,
    listener: Query<&GlobalTransform, With<PlayerCamera>>,
    mut voices: Query<(
        Entity,
        &Voice,
        &mut Transform,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let paused = ui.is_some_and(|ui| crate::player::simulation_paused(&ui));
    let master = settings.map_or(1., |s| s.master_volume);
    let ear = listener.single().ok().map(|t| t.translation());
    let mut running = HashMap::new();
    for (deployed, pose) in &bodies {
        let level = ramps.levels.get(&deployed.net_id).copied().unwrap_or(0.);
        let Some(def) = catalog.deployables.iter().find(|d| d.id == deployed.def_id) else {
            continue;
        };
        if def.fuel > 0. && level > 0.01 {
            running.insert(deployed.net_id, (def, level, pose.translation()));
        }
    }
    for (entity, voice, mut pose, mut playback, sink) in &mut voices {
        let Some((def, level, at)) = running
            .remove(&voice.net_id)
            .filter(|_| voice.epoch == ramps.epoch)
        else {
            commands.entity(entity).try_despawn();
            continue;
        };
        let load = load_fraction(def, &accepted, voice.net_id);
        let pitch = (0.6 + 0.4 * level) - load * 0.22 * level;
        let db = -26. + 20. * level + load * 3.5 * level;
        let distance = ear.map_or(RANGE, |e| e.distance(at));
        let volume =
            Volume::Linear(crate::sound::distance_gain(db, UNIT, RANGE, distance) * master);
        pose.translation = at;
        playback.volume = volume;
        playback.speed = pitch;
        playback.paused = paused;
        if let Some(mut sink) = sink {
            sink.set_volume(volume);
            sink.set_speed(pitch);
            if paused {
                sink.pause();
            } else {
                sink.play();
            }
        }
    }
    let path = "sounds/generator_engine.wav";
    // Headless hosts without an audio backend keep the level, not a voice.
    if running.is_empty() || audio.is_none() || !config.content_root.join(path).is_file() {
        return;
    }
    let clip = assets.load(path);
    for (net_id, (_, _, at)) in running {
        commands.spawn((
            Name::new("Generator engine"),
            Voice {
                net_id,
                epoch: ramps.epoch,
            },
            AudioPlayer::new(clip.clone()),
            PlaybackSettings::LOOP
                .with_spatial(true)
                .with_spatial_scale(SpatialScale::new(0.5 / RANGE))
                .with_volume(Volume::SILENT),
            Transform::from_translation(at),
        ));
    }
}

/// The non-source shake strawberry asked for, kept by the source: the body mesh
/// jitters with `_powerLevel`, harder under load. Never the turbine.
fn shake(
    mut commands: Commands,
    time: Res<Time>,
    ui: Option<Res<UiState>>,
    ramps: Res<Ramps>,
    accepted: Res<wires::Accepted>,
    catalog: Res<ItemCatalog>,
    bodies: Query<&Deployed>,
    mut meshes: Query<(
        Entity,
        &BodyMesh,
        &ChildOf,
        &mut Transform,
        Option<&mut Shake>,
    )>,
) {
    let dt = if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        0.
    } else {
        time.delta_secs()
    };
    for (entity, mesh, parent, mut pose, shake) in &mut meshes {
        let Some(def) = bodies
            .get(parent.parent())
            .ok()
            .and_then(|d| catalog.deployables.iter().find(|def| def.id == d.def_id))
        else {
            continue;
        };
        if def.fuel <= 0. || def.is_wind_turbine {
            continue;
        }
        let level = ramps.levels.get(&mesh.net_id).copied().unwrap_or(0.);
        let Some(mut shake) = shake else {
            commands.entity(entity).insert(Shake::default());
            continue;
        };
        if level > 0.01 {
            let load = load_fraction(def, &accepted, mesh.net_id);
            shake.phase += dt * (90. + load * 70.);
            let p = shake.phase;
            pose.translation = Vec3::new((p * 1.3).sin(), p.sin(), (p * 0.7).sin())
                * 0.006
                * level
                * (1. + load * 2.5);
        } else if pose.translation != Vec3::ZERO {
            pose.translation = Vec3::ZERO;
        }
    }
}
