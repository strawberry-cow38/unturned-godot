//! PlayerController.NpcTurretFx / FireTurret. Only accepted shot facts create
//! these world-space pictures and reports; held-weapon feedback is independent.
use crate::{
    entities::{CoreEvent, ItemCatalog},
    particles::{self, ParticleAssets, Spec},
    player::{GameSnapshot, PlayerCamera, WorldSessionEpoch, simulation_paused},
    settings::Settings,
    ui::{UiModel, UiState},
    vehicle::VehicleBody,
    world::WorldConfig,
};
use bevy::{
    camera::visibility::NoFrustumCulling,
    image::{ImageLoaderSettings, ImageSampler},
    light::NotShadowCaster,
    prelude::*,
};
use std::collections::HashMap;

mod material;
#[cfg(test)]
mod tests;
mod voices;

pub(in crate::vehicle) struct MountedEffectsPlugin;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct MountedEffectsUpdate;
impl Plugin for MountedEffectsPlugin {
    fn build(&self, app: &mut App) {
        app.add_plugins(material::FlashMaterialPlugin)
            .init_resource::<Effects>()
            .add_systems(Startup, prepare)
            .add_systems(
                Update,
                (retire, receive)
                    .chain()
                    .in_set(MountedEffectsUpdate)
                    .after(crate::entities::CoreEventsReady)
                    .after(crate::player::PlayerCameraUpdate)
                    .before(crate::player::flinch::FlinchUpdate),
            )
            .add_systems(
                PostUpdate,
                voices::update
                    .after(TransformSystems::Propagate)
                    .before(bevy::camera::visibility::VisibilitySystems::VisibilityPropagate),
            );
    }
}
#[derive(Resource, Default)]
struct Effects {
    epoch: u64,
    last_event: u64,
    reports: HashMap<String, f64>,
    clips: HashMap<(String, bool), Option<String>>,
    random: u64,
}
#[derive(Resource)]
struct Art {
    flash: Handle<Mesh>,
    material: Handle<material::FlashMaterial>,
}
#[derive(Component)]
struct Transient;
#[derive(Component)]
struct Lifetime(f32);
#[derive(Component)]
struct Flash;
fn prepare(
    mut commands: Commands,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<material::FlashMaterial>>,
) {
    commands.insert_resource(Art {
        flash: meshes.add(Rectangle::new(2.6, 2.6)),
        material: materials.add(material::source_material(
            assets
                .load_builder()
                .with_settings(|s: &mut ImageLoaderSettings| {
                    s.sampler = ImageSampler::linear();
                })
                .load("muzzleflash.png"),
        )),
    });
}
fn clip(fx: &mut Effects, root: &std::path::Path, gun: &str, reload: bool) -> Option<String> {
    fx.clips
        .entry((gun.to_owned(), reload))
        .or_insert_with(|| {
            let own = format!("{gun}_{}.ogg", if reload { "reload" } else { "shoot" });
            if root.join(&own).is_file() {
                Some(own)
            } else if !reload && root.join("nykorev_shoot.ogg").is_file() {
                Some("nykorev_shoot.ogg".into())
            } else {
                None
            }
        })
        .clone()
}
fn retire(
    mut commands: Commands,
    time: Res<Time>,
    epoch: Res<WorldSessionEpoch>,
    model: Res<UiModel>,
    ui: Res<UiState>,
    mut fx: ResMut<Effects>,
    roots: Query<Entity, With<Transient>>,
    mut lifetime: Query<(Entity, &mut Lifetime)>,
) {
    if fx.epoch != epoch.0 || !model.connected {
        for entity in &roots {
            commands.entity(entity).try_despawn();
        }
        *fx = Effects {
            epoch: epoch.0,
            ..default()
        };
        return;
    }
    if simulation_paused(&ui) {
        return;
    }
    for (entity, mut life) in &mut lifetime {
        life.0 -= time.delta_secs();
        if life.0 <= 0. {
            commands.entity(entity).try_despawn();
        }
    }
}
#[allow(clippy::too_many_arguments)]
fn receive(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    mut fx: ResMut<Effects>,
    art: Res<Art>,
    assets: Res<AssetServer>,
    catalog: Res<ItemCatalog>,
    config: Res<WorldConfig>,
    model: Res<UiModel>,
    snapshot: Res<GameSnapshot>,
    time: Res<Time<Real>>,
    ui: Res<UiState>,
    particles: Res<ParticleAssets>,
    mut meshes: ResMut<Assets<Mesh>>,
    vehicles: Query<(Entity, &VehicleBody)>,
    mut flinch: MessageWriter<crate::player::flinch::ExplosionFlinch>,
) {
    for event in events.read() {
        if event.sequence <= fx.last_event {
            continue;
        }
        fx.last_event = event.sequence;
        if !model.connected || event.kind != "mountShot" {
            continue;
        }
        let (Some(origin), Some(direction), Some(gun)) = (
            crate::player::vector(&event.data["muzzle"]),
            crate::player::vector(&event.data["direction"]).and_then(Vec3::try_normalize),
            event.data["gun"].as_str(),
        ) else {
            continue;
        };
        // Restrict metadata/audio to source catalog keys. NPC source reports use
        // scale1 even for a gun whose .dat carries a warhead.
        let Some(radius) = catalog.mounted_weapons.get(gun) else {
            continue;
        };
        let cannon = event.data["npc"] != true && radius.is_finite() && *radius > 0.;
        let scale = if cannon { 2.2_f32 } else { 1. };
        fx.random = fx.random.max(1);
        let roll = particles::random(&mut fx.random) * 6.28318;
        let root = commands
            .spawn((
                Name::new(format!("Accepted mounted flash {gun}")),
                Transient,
                Lifetime(0.05),
                Transform::from_translation(origin + direction * 0.35 * scale),
                Visibility::Inherited,
            ))
            .id();
        commands.spawn((
            Mesh3d(art.flash.clone()),
            MeshMaterial3d(art.material.clone()),
            Transform::from_scale(Vec3::splat(scale)),
            Flash,
            material::roll_tag(roll),
            // The vertex shader rotates into each current view, outside the
            // flat source mesh AABB. Its 50ms lifetime bounds this tiny draw.
            NoFrustumCulling,
            NotShadowCaster,
            ChildOf(root),
        ));
        commands.spawn((
            Name::new("Original mounted muzzle light"),
            PointLight {
                color: Color::srgb(0.941, 0.756, 0.152),
                range: 12. * scale.sqrt(),
                intensity: crate::hdr::point_lumens(if cannon { 5. } else { 1.5 }),
                shadow_maps_enabled: false,
                ..default()
            },
            Transform::IDENTITY,
            ChildOf(root),
        ));
        let now = time.elapsed_secs_f64();
        if fx.reports.get(gun).is_none_or(|last| now - last >= 0.04) {
            fx.reports.insert(gun.into(), now);
            if let Some(path) = clip(&mut fx, &config.content_root, gun, false) {
                voices::spawn(
                    &mut commands,
                    &assets,
                    &path,
                    origin,
                    None,
                    if cannon { 12. } else { 9. },
                    34. * scale,
                    650. * scale,
                    simulation_paused(&ui),
                );
            }
        }
        if !cannon {
            continue;
        }
        let tint = Color::srgba(0.55, 0.52, 0.46, 0.85).to_linear().to_vec4();
        let smoke = particles::spawn(
            &mut commands,
            &mut meshes,
            &particles,
            Spec {
                amount: 12,
                life: 0.9,
                one_shot: true,
                spread: 35.,
                speed: Vec2::new(3., 7.),
                gravity: Vec3::Y * 0.6,
                damping: Vec2::splat(3.),
                size: Vec2::new(0.35 * scale, 0.35 * scale * 1.8),
                colors: vec![(0., tint), (1., Vec4::new(tint.x, tint.y, tint.z, 0.))],
                growth: vec![(0., 0.55), (1., 1.)],
                direction,
                ..default()
            },
            origin + direction * 0.6,
            None,
            event.sequence,
        );
        commands
            .entity(smoke)
            .insert((Name::new("Original cannon propellant smoke"), Transient));
        if event.data["operatorPlayerId"] == snapshot.0["playerId"] {
            flinch.write(crate::player::flinch::ExplosionFlinch {
                point: origin,
                radius: 40.,
                magnitude: 4.,
            });
        }
        if let Some((vehicle, _)) = vehicles
            .iter()
            .find(|(_, v)| Some(v.net_id as u64) == event.data["vehicleNetId"].as_u64())
        {
            if let Some(path) = clip(&mut fx, &config.content_root, gun, true) {
                voices::spawn(
                    &mut commands,
                    &assets,
                    &path,
                    Vec3::ZERO,
                    Some(vehicle),
                    2.,
                    6.,
                    40.,
                    simulation_paused(&ui),
                );
            }
        }
    }
}
