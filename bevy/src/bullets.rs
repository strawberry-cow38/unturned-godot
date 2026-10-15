//! The original client-only bullet picture. Flight comes from the managed source
//! adapter; meshes and audio here never emit damage or impact facts.
use crate::{
    bridge::CoreBridge,
    content::ContentCache,
    entities::ItemCatalog,
    player::{GameSnapshot, WorldSessionEpoch},
    settings::Settings,
    ui::{UiModel, UiState},
    world::{OceanBox, WorldConfig},
};
use bevy::{
    asset::RenderAssetUsages,
    audio::{AudioSinkPlayback, SpatialScale, Volume},
    camera::primitives::Aabb,
    light::NotShadowCaster,
    mesh::{Indices, PrimitiveTopology},
    prelude::*,
};
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

pub struct BulletPlugin;
impl Plugin for BulletPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Flights>()
            .init_resource::<WaterUpload>()
            .add_systems(FixedUpdate, sync.after(crate::projectiles::ProjectileSync))
            .add_systems(
                Update,
                register_water.after(crate::entities::WorldServicesReady),
            )
            .add_systems(
                PostUpdate,
                audio.after(bevy::transform::TransformSystems::Propagate),
            );
    }
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Visuals {
    tracer_texture: String,
    max_length: f32,
    head_fraction: f32,
    segments: usize,
    pub muzzle_anchor_depth: f32,
    rocket_mesh: String,
    rocket_mesh_rotation: [f32; 3],
    rocket_color: [f32; 3],
    rocket_roughness: f32,
    rocket_sound: String,
    rocket_volume_db: f32,
    rocket_unit_size: f32,
    rocket_max_distance: f32,
}
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Flight {
    id: u64,
    #[serde(default)]
    launch_tick: i64,
    position: [f32; 3],
    origin: [f32; 3],
    velocity: [f32; 3],
    muzzle_anchor: Option<[f32; 3]>,
    silenced: bool,
    tracer_width: f32,
    tracer_color: [f32; 3],
    rocket: bool,
}
#[derive(Default, Resource)]
struct Flights {
    nodes: HashMap<u64, Node>,
    epoch: u64,
}
struct Node {
    entity: Entity,
    tracer: Option<(Entity, Handle<Mesh>)>,
    rocket: Option<Entity>,
}
#[derive(Component)]
struct RocketVoice {
    gain: f32,
    max_distance: f32,
}
#[derive(Default, Resource)]
struct WaterUpload {
    epoch: u64,
    last: Option<Value>,
}
fn register_water(
    bridge: Res<CoreBridge>,
    model: Res<UiModel>,
    epoch: Res<WorldSessionEpoch>,
    oceans: Query<(&OceanBox, &Transform)>,
    mut sent: ResMut<WaterUpload>,
) {
    if !model.connected {
        return;
    }
    let boxes:Vec<_>=oceans.iter().map(|(shape,pose)|json!({"center":pose.translation.to_array(),"size":shape.size.to_array()})).collect();
    let data = json!({"waterBoxes":boxes});
    if sent.epoch == epoch.0 && sent.last.as_ref() == Some(&data) {
        return;
    }
    match bridge.request("worldCosmeticGeometry", data.clone()) {
        Ok(_) => {
            sent.epoch = epoch.0;
            sent.last = Some(data);
        }
        Err(error) => warn!("Cannot publish cosmetic ocean geometry: {error}"),
    }
}
/// Godot TryMuzzleScreenPos -> world Camera.ProjectPosition. Depth is distance
/// along the world camera's -Z plane, not normalized-ray length. Both native
/// cameras use the same viewport rectangle, so NDC is the exact pixel bridge.
pub(crate) fn muzzle_projection(
    muzzle: Vec3,
    viewmodel: Mat4,
    view_clip: Mat4,
    world_clip: Mat4,
    world_rotation: Quat,
    depth: f32,
) -> Option<Vec3> {
    let eye = viewmodel.inverse().transform_point3(muzzle);
    if !eye.is_finite() || eye.z >= 0. || !depth.is_finite() || depth <= 0. {
        return None;
    }
    let ndc = view_clip.project_point3(eye);
    let near = world_clip
        .inverse()
        .project_point3(Vec3::new(ndc.x, ndc.y, 1.));
    let offset = world_rotation * (near * (depth / -near.z));
    offset.is_finite().then_some(offset)
}
pub(crate) fn fire_context(alpha: f32, muzzle_offset: Option<Vec3>, origin: Vec3) -> Value {
    let mut visual = json!({"aimAlpha":alpha});
    if let Some(offset) = muzzle_offset {
        visual["muzzleAnchor"] = json!((origin + offset).to_array());
    }
    visual
}
/// Source UpdateTracer: two fixed perpendicular triangle strips, pointed tail
/// and circular nose cap. Vertices are relative to the head so origin rebases
/// shift the root once without rewriting mesh data.
fn tracer(flight: &Flight, visual: &Visuals) -> Option<Mesh> {
    let head = Vec3::from_array(flight.position);
    let muzzle = Vec3::from_array(flight.muzzle_anchor.unwrap_or(flight.origin));
    let segment = head - muzzle;
    let distance = segment.length();
    if !distance.is_finite() || distance < 0.1 || flight.silenced {
        return None;
    }
    let dir = segment / distance;
    let len = visual.max_length.min(distance);
    let aux = if dir.dot(Vec3::Y).abs() > 0.95 {
        Vec3::X
    } else {
        Vec3::Y
    };
    let first = dir.cross(aux).normalize();
    let second = dir.cross(first).normalize();
    let mut vertices = Vec::<[f32; 3]>::new();
    let mut uv = vec![];
    let mut indices = vec![];
    let n = visual.segments.max(1);
    let t_head = visual.head_fraction;
    for perp in [first, second] {
        let base = vertices.len() as u32;
        for i in 0..=n {
            let t = i as f32 / n as f32;
            let width = if t < t_head {
                flight.tracer_width * (t / t_head)
            } else {
                flight.tracer_width * (1. - ((t - t_head) / (1. - t_head)).powi(2)).max(0.).sqrt()
            };
            let p = -dir * len * (1. - t);
            let off = perp * width;
            vertices.extend([(p - off).to_array(), (p + off).to_array()]);
            uv.extend([[t, 0.], [t, 1.]]);
            if i < n {
                let a = base + 2 * i as u32;
                indices.extend([a, a + 1, a + 2, a + 2, a + 1, a + 3]);
            }
        }
    }
    Some(
        Mesh::new(
            PrimitiveTopology::TriangleList,
            RenderAssetUsages::default(),
        )
        .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, vertices)
        .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, vec![[0., 1., 0.]; 4 * (n + 1)])
        .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, uv)
        .with_inserted_indices(Indices::U32(indices)),
    )
}
fn empty_mesh() -> Mesh {
    // A hidden just-launched tracer still needs a valid renderer asset. The
    // source ImmediateMesh has no surface until it travels at least10cm.
    Mesh::new(
        PrimitiveTopology::TriangleList,
        RenderAssetUsages::default(),
    )
    .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, vec![[0.; 3]; 3])
    .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, vec![[0., 1., 0.]; 3])
    .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, vec![[0.; 2]; 3])
    .with_inserted_indices(Indices::U32(vec![0, 1, 2]))
}
fn orientation(velocity: Vec3) -> Option<Quat> {
    let direction = velocity.try_normalize()?;
    (direction.y.abs() < 0.98).then(|| Transform::IDENTITY.looking_to(direction, Vec3::Y).rotation)
}
#[allow(clippy::too_many_arguments)]
fn sync(
    mut commands: Commands,
    mut flights: ResMut<Flights>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    settings: Res<Settings>,
    ui: Res<UiState>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut poses: Query<&mut Transform>,
    mut visibility: Query<&mut Visibility>,
) {
    if flights.epoch != epoch.0 {
        for (_, node) in flights.nodes.drain() {
            commands.entity(node.entity).try_despawn();
        }
        flights.epoch = epoch.0;
    }
    let Some(visual) = &catalog.bullet_visuals else {
        return;
    };
    let mut seen = HashSet::new();
    for value in snapshot.0["bullets"].as_array().into_iter().flatten() {
        let Ok(flight) = serde_json::from_value::<Flight>(value.clone()) else {
            continue;
        };
        if !Vec3::from_array(flight.position).is_finite() {
            continue;
        }
        seen.insert(flight.id);
        let node = flights.nodes.entry(flight.id).or_insert_with(|| {
            let entity = commands
                .spawn((
                    Name::new(format!("Cosmetic bullet {}", flight.id)),
                    Transform::from_translation(Vec3::from_array(flight.position)),
                    Visibility::Inherited,
                ))
                .id();
            let tracer_node = if flight.silenced {
                None
            } else {
                let mesh = meshes.add(tracer(&flight, visual).unwrap_or_else(empty_mesh));
                let material = materials.add(StandardMaterial {
                    base_color: Color::srgb(
                        flight.tracer_color[0],
                        flight.tracer_color[1],
                        flight.tracer_color[2],
                    ),
                    base_color_texture: config
                        .content_root
                        .join(&visual.tracer_texture)
                        .is_file()
                        .then(|| assets.load(visual.tracer_texture.clone())),
                    unlit: true,
                    alpha_mode: AlphaMode::Add,
                    cull_mode: None,
                    ..default()
                });
                let child = commands
                    .spawn((
                        Name::new("Source crossed teardrop tracer"),
                        Mesh3d(mesh.clone()),
                        MeshMaterial3d(material),
                        Transform::IDENTITY,
                        Visibility::Inherited,
                        NotShadowCaster,
                        ChildOf(entity),
                    ))
                    .id();
                Some((child, mesh))
            };
            let rocket = if flight.rocket {
                cache
                    .load_mesh(&config.content_root.join(&visual.rocket_mesh), &mut meshes)
                    .ok()
                    .map(|(mesh, _)| {
                        let holder = commands
                            .spawn((
                                Name::new("Original rocket projectile"),
                                Transform::IDENTITY,
                                Visibility::Inherited,
                                ChildOf(entity),
                            ))
                            .id();
                        let color = visual.rocket_color;
                        let material = materials.add(StandardMaterial {
                            base_color: Color::srgb(color[0], color[1], color[2]),
                            perceptual_roughness: visual.rocket_roughness,
                            ..default()
                        });
                        let angles =
                            Vec3::from_array(visual.rocket_mesh_rotation).map(f32::to_radians);
                        commands.spawn((
                            Mesh3d(mesh),
                            MeshMaterial3d(material),
                            Transform::from_rotation(Quat::from_euler(
                                EulerRot::YXZ,
                                angles.y,
                                angles.x,
                                angles.z,
                            )),
                            ChildOf(holder),
                        ));
                        if config.content_root.join(&visual.rocket_sound).is_file() {
                            let gain = 10_f32.powf(visual.rocket_volume_db / 20.);
                            commands.spawn((
                                AudioPlayer::new(assets.load(visual.rocket_sound.clone())),
                                PlaybackSettings {
                                    paused: crate::player::simulation_paused(&ui),
                                    ..PlaybackSettings::LOOP
                                }
                                .with_spatial(true)
                                .with_spatial_scale(SpatialScale::new(1. / visual.rocket_unit_size))
                                .with_volume(Volume::Linear(gain * settings.master_volume)),
                                RocketVoice {
                                    gain,
                                    max_distance: visual.rocket_max_distance,
                                },
                                Transform::IDENTITY,
                                ChildOf(holder),
                            ));
                        }
                        holder
                    })
            } else {
                None
            };
            Node {
                entity,
                tracer: tracer_node,
                rocket,
            }
        });
        if let Ok(mut pose) = poses.get_mut(node.entity) {
            pose.translation = Vec3::from_array(flight.position);
        }
        if let Some((child, mesh)) = &node.tracer {
            let shape = tracer(&flight, visual);
            let visible = shape.is_some();
            if let Some(shape) = shape {
                if let Some(mut asset) = meshes.get_mut(mesh) {
                    *asset = shape;
                }
                // Dynamic mesh bounds change as the tail advances.
                commands.entity(*child).remove::<Aabb>();
            }
            if let Ok(mut current) = visibility.get_mut(*child) {
                *current = if visible {
                    Visibility::Inherited
                } else {
                    Visibility::Hidden
                };
            } else {
                commands.entity(*child).insert(if visible {
                    Visibility::Inherited
                } else {
                    Visibility::Hidden
                });
            }
        }
        if let Some(rocket) = node.rocket {
            // SpawnRocketVis starts with an identity holder; StepBullets aims
            // it only after the first actual flight step.
            if snapshot.0["tick"].as_i64().unwrap_or(0) <= flight.launch_tick {
                continue;
            }
            if let Some(rotation) = orientation(Vec3::from_array(flight.velocity)) {
                if let Ok(mut pose) = poses.get_mut(rocket) {
                    pose.rotation = rotation;
                } else {
                    commands
                        .entity(rocket)
                        .insert(Transform::from_rotation(rotation));
                }
            }
        }
    }
    flights.nodes.retain(|id, node| {
        if seen.contains(id) {
            true
        } else {
            commands.entity(node.entity).try_despawn();
            false
        }
    });
}
fn audio(
    settings: Res<Settings>,
    ui: Res<UiState>,
    listeners: Query<&GlobalTransform, With<SpatialListener>>,
    mut voices: Query<(
        &RocketVoice,
        &GlobalTransform,
        &mut PlaybackSettings,
        Option<&mut SpatialAudioSink>,
    )>,
) {
    let listener = listeners.iter().next().map(GlobalTransform::translation);
    let paused = crate::player::simulation_paused(&ui);
    for (voice, pose, mut playback, sink) in &mut voices {
        let audible = listener.is_none_or(|p| p.distance(pose.translation()) < voice.max_distance);
        let volume = Volume::Linear(if audible {
            voice.gain * settings.master_volume
        } else {
            0.
        });
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
mod tests;
