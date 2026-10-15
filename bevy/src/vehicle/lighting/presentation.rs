use super::{
    geometry::{BuiltLighting, Kind},
    state::{Accepted, Clock},
};
use crate::{
    bridge::CoreBridge,
    entities::{CoreEvent, ItemCatalog},
    hdr::SourceFalloff,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::Settings,
    ui::{UiModel, UiState},
    vehicle::{VehicleBody, audio, data},
    viewmodel::lighting::WorldLight,
    world::{WorldConfig, WorldState},
};
use bevy::{
    audio::{AudioSinkPlayback, Volume},
    prelude::*,
};
use serde_json::json;
use std::collections::HashSet;
#[cfg(test)]
mod tests;

#[derive(Clone, Copy)]
enum Role {
    Head,
    Tail,
    Fill,
    Siren(usize),
    Nav,
    Beacon,
}
struct Light {
    entity: Entity,
    role: Role,
    bound: Option<u8>,
    energy: f32,
}
#[derive(Component, Default)]
pub(super) struct Runtime {
    clock: Clock,
    received_state: bool,
    lights: Vec<Light>,
    voice: Option<Entity>,
    uploaded: Option<(u64, u32)>,
}
fn slot(catalog: &super::Definition, label: &str) -> Option<u8> {
    catalog
        .lamp_labels
        .iter()
        .position(|l| l == label)
        .map(|i| i as u8)
}

pub(super) fn build(
    mut commands: Commands,
    bodies: Query<(Entity, &BuiltLighting), (With<VehicleBody>, Without<Runtime>)>,
    catalog: Res<ItemCatalog>,
) {
    let Some(catalog) = &catalog.vehicle_lighting else {
        return;
    };
    for (body, built) in &bodies {
        let mut runtime = Runtime::default();
        let mut bound = HashSet::new();
        for (positions, head) in [(&built.head_spots, true), (&built.tail_spots, false)] {
            for p in positions {
                let label = match (head, p.x < 0.) {
                    (true, true) => "headlight_l",
                    (true, false) => "headlight_r",
                    (false, true) => "taillight_l",
                    (false, false) => "taillight_r",
                };
                // Source binds only the first emitter matching a lamp's side.
                let bound_slot = built
                    .lamps
                    .iter()
                    .any(|l| l.label == label)
                    .then(|| slot(catalog, label))
                    .flatten()
                    .filter(|s| bound.insert(*s));
                let light = SpotLight {
                    color: if head {
                        built.head_tint
                    } else {
                        Color::srgb(0.996, 0., 0.)
                    },
                    intensity: 0.,
                    range: if head { catalog.low.range } else { 3. },
                    outer_angle: if head { catalog.low.angle } else { 72. }.to_radians(),
                    inner_angle: 0.,
                    shadow_maps_enabled: head,
                    ..default()
                };
                let mut node = commands.spawn((
                    Name::new(label),
                    light,
                    if head {
                        SourceFalloff::new(1., catalog.low.attenuation).with_headlight_fade()
                    } else {
                        SourceFalloff::new(1., 0.6)
                    },
                    Transform::from_translation(*p).with_rotation(if head {
                        Quat::IDENTITY
                    } else {
                        Quat::from_rotation_y(std::f32::consts::PI)
                    }),
                    Visibility::Hidden,
                    ChildOf(body),
                ));
                if head {
                    node.insert(WorldLight);
                }
                runtime.lights.push(Light {
                    entity: node.id(),
                    role: if head { Role::Head } else { Role::Tail },
                    bound: bound_slot,
                    energy: 2.2,
                });
            }
        }
        if let Some(p) = built.head_fill {
            let entity = commands
                .spawn((
                    Name::new("Headlight fill"),
                    PointLight {
                        color: built.head_tint,
                        intensity: 0.,
                        range: 28.,
                        shadow_maps_enabled: false,
                        ..default()
                    },
                    Transform::from_translation(p),
                    Visibility::Hidden,
                    ChildOf(body),
                    WorldLight,
                ))
                .id();
            runtime.lights.push(Light {
                entity,
                role: Role::Fill,
                bound: None,
                energy: 0.8,
            });
        }
        for lens in &built.lenses {
            let Some(emitter) = &lens.emitter else {
                continue;
            };
            let role = match lens.kind {
                Kind::SirenLeft => Role::Siren(0),
                Kind::SirenRight => Role::Siren(1),
                Kind::NavPort | Kind::NavStarboard => Role::Nav,
                Kind::Beacon => Role::Beacon,
                _ => continue,
            };
            let mut node = commands.spawn((
                Name::new(format!("Vehicle {:?} emitter", lens.kind)),
                PointLight {
                    color: emitter.color,
                    intensity: 0.,
                    range: emitter.range,
                    shadow_maps_enabled: false,
                    ..default()
                },
                Transform::from_translation(emitter.position),
                Visibility::Hidden,
                ChildOf(body),
            ));
            if matches!(role, Role::Siren(_)) {
                node.insert((WorldLight, SourceFalloff::new(1.5, 1.)));
            }
            runtime.lights.push(Light {
                entity: node.id(),
                role,
                bound: None,
                energy: emitter.energy,
            });
        }
        commands.entity(body).insert(runtime);
    }
}

pub(super) fn upload(
    bridge: Res<CoreBridge>,
    model: Res<UiModel>,
    world: Res<WorldState>,
    epoch: Res<WorldSessionEpoch>,
    mut bodies: Query<(&VehicleBody, &BuiltLighting, &mut Runtime)>,
) {
    if !model.connected || !model.network.owns_world || !world.ready {
        return;
    }
    for (body, built, mut state) in &mut bodies {
        let key = (epoch.0, body.net_id);
        if body.net_id == 0 || state.uploaded == Some(key) {
            continue;
        }
        let lamps: Vec<_> = built
            .lamps
            .iter()
            .map(|l| json!({"label":l.label,"center":l.center.to_array()}))
            .collect();
        match bridge.request(
            "worldVehicleLighting",
            json!({"netId":body.net_id,"lamps":lamps}),
        ) {
            Ok(_) => state.uploaded = Some(key),
            Err(e) => warn!("Vehicle lamp geometry {}: {e}", body.net_id),
        }
    }
}

pub(super) fn advance(
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    ui: Option<Res<UiState>>,
    mut bodies: Query<(&VehicleBody, &mut Runtime)>,
) {
    if ui.as_deref().is_some_and(crate::player::simulation_paused) {
        return;
    }
    let Some(catalog) = &catalog.vehicle_lighting else {
        return;
    };
    let Some(rows) = snapshot.0["vehicles"].as_array() else {
        return;
    };
    for (body, mut runtime) in &mut bodies {
        let Some(row) = rows
            .iter()
            .find(|r| r["netId"].as_u64() == Some(body.net_id as u64))
        else {
            continue;
        };
        runtime.received_state = true;
        runtime.clock.adopt(Accepted::read(row));
        let heli = data::definition(body.type_id).is_some_and(|d| d.spec.heli);
        runtime.clock.step(
            0.02,
            heli,
            body.flight.spool,
            body.health,
            &catalog.siren_pitch,
        );
    }
}

pub(super) fn update(
    mut commands: Commands,
    mut bodies: Query<(
        Entity,
        &BuiltLighting,
        &mut Runtime,
        &mut super::breaks::BreakColors,
    )>,
    mut lights: Query<(
        Option<&mut PointLight>,
        Option<&mut SpotLight>,
        &mut Visibility,
    )>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut voices: Query<(
        &mut audio::VehicleSound,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
    catalog: Res<ItemCatalog>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
) {
    let Some(catalog) = &catalog.vehicle_lighting else {
        return;
    };
    for (body, built, mut runtime, mut break_colors) in &mut bodies {
        let clock = &runtime.clock;
        let state = clock.accepted;
        for lens in &built.lenses {
            let Some(mut material) = materials.get_mut(&lens.material) else {
                continue;
            };
            let lamp = built.lamps.iter().find(|l| l.entity == lens.entity);
            let broken = lamp
                .and_then(|l| slot(catalog, l.label))
                .is_some_and(|s| state.broken(s));
            let mut color = lens.color;
            let mut energy = 0.;
            let mut albedo = lens.color;
            match lens.kind {
                Kind::Head => {
                    color = built.head_tint;
                    if state.head() && !broken {
                        energy = if built.head_primary.as_ref() == Some(&lens.material) {
                            clock.primary_head_energy
                        } else {
                            2.
                        };
                    }
                    if lamp.is_some() {
                        albedo = Color::srgb(0.94, 0.89, 0.73);
                    }
                }
                Kind::Tail => {
                    color = Color::srgb(0.56, 0.13, 0.13);
                    if state.tail() && !broken {
                        energy = if state.flags & 16 != 0 { 6. } else { 2. };
                    }
                    if lamp.is_some() {
                        albedo = Color::srgb(0.42, 0.06, 0.06);
                    }
                }
                Kind::SirenLeft | Kind::SirenRight => {
                    let side = usize::from(lens.kind == Kind::SirenRight);
                    color = if side == 0 {
                        Color::srgb(1., 0.05, 0.05)
                    } else {
                        Color::srgb(0.1, 0.15, 1.)
                    };
                    if clock.siren_lit[side] {
                        energy = 4.;
                    }
                    if clock.dark_bar[side] || lamp.is_some_and(|l| break_colors.dark_bar(l.label))
                    {
                        albedo = Color::srgb(0.12, 0.12, 0.12);
                    }
                }
                Kind::NavPort | Kind::NavStarboard => {
                    if clock.nav_on {
                        energy = 2.6;
                    }
                }
                Kind::Beacon => {
                    energy = if clock.beacon_lit { 6. } else { 0. };
                    albedo = if clock.beacon_lit {
                        Color::srgb(1., 0.35, 0.35)
                    } else {
                        Color::srgb(0.28, 0.05, 0.05)
                    };
                }
            }
            if broken {
                albedo = Color::srgb(0.12, 0.12, 0.12);
                energy = 0.;
            }
            if state.wreck() {
                energy = 0.;
            }
            if runtime.received_state
                && let Some(lamp) = lamp
            {
                break_colors.observe(lamp.label, broken, material.base_color, albedo);
            }
            material.base_color = albedo;
            material.emissive = color.to_linear() * energy;
        }
        for light in &runtime.lights {
            let Ok((point, spot, mut visibility)) = lights.get_mut(light.entity) else {
                continue;
            };
            let on = match light.role {
                Role::Head => state.head(),
                Role::Tail => state.tail(),
                Role::Fill => state.head() && !(state.broken(0) && state.broken(1)),
                Role::Siren(side) => clock.siren_lit[side],
                Role::Nav => clock.nav_on,
                Role::Beacon => clock.beacon_lit,
            } && !light.bound.is_some_and(|s| state.broken(s));
            *visibility = if on {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            };
            if let Some(mut spot) = spot {
                let energy = if matches!(light.role, Role::Head) {
                    let beam = if state.high {
                        &catalog.high
                    } else {
                        &catalog.low
                    };
                    spot.range = beam.range;
                    spot.outer_angle = beam.angle.to_radians();
                    commands
                        .entity(light.entity)
                        .insert(SourceFalloff::new(1., beam.attenuation).with_headlight_fade());
                    beam.energy
                } else {
                    light.energy
                };
                spot.intensity = crate::hdr::point_lumens(if on { energy } else { 0. });
            }
            if let Some(mut point) = point {
                point.intensity = crate::hdr::point_lumens(if on { light.energy } else { 0. });
            }
        }
        let audible = clock.audible();
        let gain = 10_f32.powf(clock.siren_db / 20.);
        let pitch = clock.siren_pitch;
        if audible {
            if let Some(entity) = runtime.voice {
                if let Ok((mut sound, mut playback, sink)) = voices.get_mut(entity) {
                    sound.gain = gain;
                    playback.speed = pitch;
                    playback.volume = Volume::Linear(gain * settings.master_volume);
                    if let Some(sink) = sink {
                        sink.set_speed(pitch);
                    }
                }
            } else if config.content_root.join("siren.wav").is_file() {
                runtime.voice = Some(audio::spawn_sound(
                    &mut commands,
                    &assets,
                    body,
                    "siren.wav",
                    true,
                    gain,
                    pitch,
                    14.,
                    120.,
                    settings.master_volume,
                ));
            }
        } else if let Some(entity) = runtime.voice.take() {
            if let Ok(mut entity) = commands.get_entity(entity) {
                entity.despawn();
            }
        }
    }
}

pub(super) fn events(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    bodies: Query<(Entity, &VehicleBody)>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
) {
    for event in events.read().filter(|e| e.kind == "vehicleAlarmHorn") {
        let Some(id) = event.data["netId"].as_u64() else {
            continue;
        };
        let Some((entity, body)) = bodies.iter().find(|(_, b)| b.net_id as u64 == id) else {
            continue;
        };
        let Some(spec) = data::definition(body.type_id).map(|d| &d.spec) else {
            continue;
        };
        let Some(path) = spec
            .horn
            .as_deref()
            .filter(|p| config.content_root.join(p).is_file())
        else {
            continue;
        };
        let range = if spec.horn_range > 0. {
            spec.horn_range
        } else {
            90.
        };
        audio::spawn_sound(
            &mut commands,
            &assets,
            entity,
            path,
            false,
            10_f32.powf(4. / 20.),
            1.,
            range / 7.5,
            range,
            settings.master_volume,
        );
    }
}
