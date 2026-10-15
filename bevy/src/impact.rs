//! Accepted server impact facts -> original ImpactFx and HitmarkerHUD presentation.
//! This module never applies damage or predicts a confirmed hit.
mod markers;
pub mod surfaces;
use crate::{
    entities::{ApplyInteractions, CoreEvent},
    particles::{self, ParticleAssets, Spec},
    player::{GameSnapshot, PlayerCamera, WorldSessionEpoch},
    settings::Settings,
    ui::UiState,
    world::WorldConfig,
};
use bevy::{
    audio::Volume,
    core_pipeline::prepass::DepthPrepass,
    pbr::decal::{ForwardDecal, ForwardDecalMaterial, ForwardDecalMaterialExt},
    prelude::*,
};
use bevy_rapier3d::prelude::*;
use std::collections::HashMap;
use surfaces::{Surface, Surfaces};

pub struct ImpactPlugin;
impl Plugin for ImpactPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Feedback>()
            .init_resource::<surfaces::SurfaceTable>()
            .add_systems(Startup, preload)
            .add_systems(
                Update,
                (surfaces::reload, receive, markers::update, decay)
                    .chain()
                    .after(ApplyInteractions),
            )
            .add_systems(Update, enable_decals);
    }
}
#[derive(Resource, Default)]
struct Feedback {
    epoch: u64,
    last_event: u64,
    materials: HashMap<String, (Handle<StandardMaterial>, bool)>,
    decals: HashMap<Surface, Handle<ForwardDecalMaterial<StandardMaterial>>>,
    sounds: HashMap<Surface, crate::sound::Bank>,
}
// Load short-lived effects before combat: asynchronous PNG upload must not eat
// the entire first burst's one-second lifetime.
fn preload(
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut feedback: ResMut<Feedback>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    feedback.material(
        "flesh",
        "impact_flesh_dynamic_0.png",
        if config
            .content_root
            .join("impact_flesh_dynamic_0.png")
            .is_file()
        {
            Color::WHITE
        } else {
            Color::srgb(0.5, 0.02, 0.02)
        },
        0.46,
        &config,
        &assets,
        &mut materials,
    );
    for surface in [
        Surface::Concrete,
        Surface::Grass,
        Surface::Dirt,
        Surface::Metal,
        Surface::Wood,
        Surface::Sand,
        Surface::Water,
        Surface::Gravel,
        Surface::Snow,
        Surface::Ice,
        Surface::Rock,
        Surface::Rails,
    ] {
        let tint = if surface == Surface::Snow {
            Color::srgb(0.96, 0.975, 1.)
        } else if surface == Surface::Water {
            Color::srgb(0.82, 0.9, 1.)
        } else {
            Color::WHITE
        };
        feedback.material(
            &format!("{surface:?}"),
            &format!("impact_{}_static_0.png", surface.debris()),
            tint,
            if surface == Surface::Water { 0.5 } else { 0.46 },
            &config,
            &assets,
            &mut materials,
        );
    }
}
#[derive(Component)]
struct Hole {
    age: f32,
}
#[derive(Component)]
struct ImpactTransient;
fn enable_decals(
    mut commands: Commands,
    cameras: Query<Entity, (With<PlayerCamera>, Without<DepthPrepass>)>,
) {
    for e in &cameras {
        commands.entity(e).insert(DepthPrepass);
    }
}
fn decay(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    mut holes: Query<(Entity, &mut Hole)>,
) {
    let dt = if crate::player::simulation_paused(&ui) {
        0.
    } else {
        time.delta_secs()
    };
    for (e, mut hole) in &mut holes {
        hole.age += dt;
        if hole.age >= 18. {
            commands.entity(e).despawn();
        }
    }
}
fn blood_spec(material: Handle<StandardMaterial>, direction: Vec3, sheet: bool) -> Spec {
    Spec {
        amount: 4,
        life: 1.,
        one_shot: true,
        direction: -direction.try_normalize().unwrap_or(Vec3::NEG_Z),
        spread: 60.,
        speed: Vec2::new(3., 6.),
        gravity: Vec3::new(0., -9.8, 0.),
        size: Vec2::new(0.125, 0.25),
        angle: Vec2::ZERO,
        colors: vec![(0., Vec4::ONE), (1., Vec4::ONE)],
        atlas: if sheet { UVec2::new(4, 1) } else { UVec2::ONE },
        animation_offset: if sheet { Vec2::new(0., 1.) } else { Vec2::ZERO },
        material: Some(material),
        lit: true,
        ..default()
    }
}
fn debris_spec(
    surface: Surface,
    material: Handle<StandardMaterial>,
    normal: Vec3,
    sheet: bool,
) -> Spec {
    let metal = surface == Surface::Metal;
    let snow = surface == Surface::Snow;
    Spec {
        amount: if metal { 7 } else { 5 },
        life: 1.,
        one_shot: true,
        direction: normal.try_normalize().unwrap_or(Vec3::Y),
        spread: 50.,
        speed: if metal {
            Vec2::new(5., 9.)
        } else if snow {
            Vec2::new(1.6, 3.4)
        } else {
            Vec2::new(3., 6.)
        },
        gravity: Vec3::new(0., if snow { -4.2 } else { -9.8 }, 0.),
        size: if metal {
            Vec2::new(0.075, 0.15)
        } else {
            Vec2::new(0.125, 0.25)
        },
        angle: Vec2::new(-180., 180.),
        spin: Vec2::new(-360., 360.),
        colors: vec![(0., Vec4::ONE), (1., Vec4::ONE)],
        atlas: if sheet { UVec2::new(4, 1) } else { UVec2::ONE },
        animation_offset: if sheet { Vec2::new(0., 1.) } else { Vec2::ZERO },
        animation_loop: sheet,
        material: Some(material),
        lit: true,
        ..default()
    }
}
fn water_spec(material: Handle<StandardMaterial>, scale: f32) -> Spec {
    let big = scale >= 2.;
    Spec {
        // Godot Mathf.RoundToInt uses ties-to-even: 10 * .25 is 2.
        amount: if big { 24 } else { 2 },
        life: if big { 2.5 } else { 1. },
        one_shot: true,
        direction: Vec3::Y,
        spread: if big { 18. } else { 45. },
        speed: if big {
            Vec2::new(8., 22.)
        } else {
            Vec2::new(3., 6.)
        },
        gravity: Vec3::new(0., -9.8, 0.),
        size: Vec2::new(0.28, 0.5) * scale * 0.25,
        radius: 0.2 * scale,
        angle: Vec2::new(-180., 180.),
        spin: Vec2::new(-220., 220.),
        colors: vec![(0., Vec4::ONE), (1., Vec4::ONE)],
        growth: vec![(0., 1.), (1., 0.)],
        material: Some(material),
        lit: true,
        ..default()
    }
}
impl Feedback {
    fn material(
        &mut self,
        key: &str,
        texture: &str,
        tint: Color,
        cutoff: f32,
        config: &WorldConfig,
        assets: &AssetServer,
        materials: &mut Assets<StandardMaterial>,
    ) -> (Handle<StandardMaterial>, bool) {
        if let Some(v) = self.materials.get(key) {
            return v.clone();
        }
        let path = config.content_root.join(texture);
        let bytes = std::fs::read(&path).ok();
        // PNG's fixed IHDR dimensions let the source's width>=3*height sheet rule
        // be decided before asynchronous GPU upload; no image decoder is duplicated.
        let sheet = bytes
            .as_ref()
            .filter(|b| b.len() >= 24 && &b[..8] == b"\x89PNG\r\n\x1a\n")
            .is_some_and(|b| {
                u32::from_be_bytes(b[16..20].try_into().unwrap())
                    >= 3 * u32::from_be_bytes(b[20..24].try_into().unwrap())
            });
        let material = materials.add(StandardMaterial {
            base_color: tint,
            base_color_texture: bytes.is_some().then(|| assets.load(texture.to_owned())),
            alpha_mode: AlphaMode::Mask(cutoff),
            perceptual_roughness: 1.,
            reflectance: 0.,
            metallic: 0.,
            cull_mode: None,
            double_sided: true,
            ..default()
        });
        self.materials.insert(key.into(), (material.clone(), sheet));
        (material, sheet)
    }
    fn sound(&mut self, surface: Surface, config: &WorldConfig, seed: u64) -> Option<String> {
        let bank = self.sounds.entry(surface).or_insert_with(|| {
            crate::sound::Bank::load(&config.content_root, "bulletimpacts", surface.bullet_bank())
        });
        bank.pick(seed).map(str::to_owned).or_else(|| {
            let path = format!("impact_{}.wav", surface.debris());
            config.content_root.join(&path).is_file().then_some(path)
        })
    }
}
fn sound(
    commands: &mut Commands,
    assets: &AssetServer,
    settings: &Settings,
    path: &str,
    point: Vec3,
) {
    let e = crate::vehicle::spawn_ranged_audio(
        commands,
        assets,
        None,
        path,
        false,
        10_f32.powf(-3. / 20.),
        1.,
        5.,
        70.,
        settings.master_volume,
    );
    commands
        .entity(e)
        .insert((Transform::from_translation(point), ImpactTransient));
}
#[allow(clippy::too_many_arguments)]
fn receive(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    mut feedback: ResMut<Feedback>,
    epoch: Res<WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    config: Res<WorldConfig>,
    settings: Res<Settings>,
    assets: Res<AssetServer>,
    particles: Res<ParticleAssets>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut decals: ResMut<Assets<ForwardDecalMaterial<StandardMaterial>>>,
    cameras: Query<(Entity, &GlobalTransform), With<PlayerCamera>>,
    rapier: ReadRapierContext,
    (surfaces, poses): (Surfaces, surfaces::CurrentPoses),
    old: Query<Entity, Or<(With<ImpactTransient>, With<markers::Marker>)>>,
) {
    if feedback.epoch != epoch.0 {
        for e in &old {
            commands.entity(e).try_despawn();
        }
        feedback.epoch = epoch.0;
        feedback.last_event = 0;
    }
    let camera = cameras.single().ok();
    let eye = camera.map_or(Vec3::ZERO, |(_, p)| p.translation());
    let player = snapshot.0["playerId"].as_u64().unwrap_or(0);
    for event in events.read() {
        if event.sequence <= feedback.last_event {
            continue;
        }
        feedback.last_event = event.sequence;
        let data = &event.data;
        let seed = event.sequence.wrapping_mul(6364136223846793005);
        let marker = match event.kind.as_str() {
            "hit" => Some(if data["headshot"].as_bool() == Some(true) {
                markers::Kind::Head
            } else {
                markers::Kind::Body
            }),
            "animalHit" | "zombieHit"
                if data["attacker"].as_u64() == Some(player)
                    && player != 0
                    && data["source"].as_str() != Some("blast")
                    && data["hitConfirmedEmitted"].as_bool() != Some(true) =>
            {
                Some(markers::Kind::Body)
            }
            "vehicleDamage" | "deployableDamage"
                if data["attacker"].as_u64() == Some(player) && player != 0 =>
            {
                Some(markers::Kind::Circle)
            }
            _ => None,
        };
        if let Some(marker) = marker {
            markers::spawn(&mut commands, marker);
            if marker == markers::Kind::Head && config.content_root.join("hit.ogg").is_file() {
                commands.spawn((
                    AudioPlayer::new(assets.load("hit.ogg")),
                    PlaybackSettings::DESPAWN.with_volume(Volume::Linear(
                        10_f32.powf(-6. / 20.) * settings.master_volume,
                    )),
                    ImpactTransient,
                ));
            }
        }
        let flesh = match event.kind.as_str() {
            "impact" => data["surface"].as_u64() == Some(0),
            "animalHit" | "zombieHit" => {
                data["impactEmitted"].as_bool() == Some(false)
                    && data["source"].as_str() == Some("melee")
            }
            _ => false,
        };
        if event.kind != "impact" && !flesh {
            continue;
        }
        let Some(point) = crate::player::vector(if event.kind == "impact" {
            &data["pos"]
        } else {
            &data["point"]
        }) else {
            continue;
        };
        let direction = crate::player::vector(&data["direction"])
            .unwrap_or(point - eye)
            .try_normalize()
            .unwrap_or(Vec3::NEG_Z);
        if flesh {
            let tint = if config
                .content_root
                .join("impact_flesh_dynamic_0.png")
                .is_file()
            {
                Color::WHITE
            } else {
                Color::srgb(0.5, 0.02, 0.02)
            };
            let (material, sheet) = feedback.material(
                "flesh",
                "impact_flesh_dynamic_0.png",
                tint,
                0.46,
                &config,
                &assets,
                &mut materials,
            );
            let e = particles::spawn(
                &mut commands,
                &mut meshes,
                &particles,
                blood_spec(material, direction, sheet),
                point,
                None,
                seed,
            );
            commands
                .entity(e)
                .insert((Name::new("Accepted flesh impact"), ImpactTransient));
            if config.content_root.join("impact_flesh.wav").is_file() {
                sound(&mut commands, &assets, &settings, "impact_flesh.wav", point);
            }
            continue;
        }
        // Original PlayerController.RenderImpactFx recovers the surface normal
        // with a one-metre local probe around the accepted point; no damage ray.
        let hit = rapier.single().ok().and_then(|context| {
            context.cast_ray_and_get_normal(
                point - direction * 0.5,
                direction,
                1.,
                true,
                QueryFilter::default()
                    .exclude_sensors()
                    .groups(CollisionGroups::new(
                        Group::ALL,
                        Group::ALL & !Group::GROUP_2 & !Group::GROUP_5 & !Group::GROUP_6,
                    )),
            )
        });
        let (point, normal, surface, parent) = if let Some((entity, hit)) = hit {
            let (s, p) = surfaces.at(entity, hit.point);
            (hit.point, hit.normal, s, p)
        } else {
            (point, Vec3::Y, Surface::Dirt, None)
        };
        let snow = surface == Surface::Snow;
        let water = surface == Surface::Water;
        let tint = if snow {
            Color::srgb(0.96, 0.975, 1.)
        } else if water {
            Color::srgb(0.82, 0.9, 1.)
        } else {
            Color::WHITE
        };
        let texture = format!("impact_{}_static_0.png", surface.debris());
        let (material, sheet) = feedback.material(
            &format!("{surface:?}"),
            &texture,
            tint,
            if water { 0.5 } else { 0.46 },
            &config,
            &assets,
            &mut materials,
        );
        let spec = if water {
            water_spec(material, 1.)
        } else {
            debris_spec(surface, material, normal, sheet)
        };
        let e = particles::spawn(
            &mut commands,
            &mut meshes,
            &particles,
            spec,
            point + if water { Vec3::ZERO } else { normal * 0.03 },
            None,
            seed,
        );
        commands.entity(e).insert((
            Name::new(format!("Accepted {surface:?} impact")),
            ImpactTransient,
        ));
        if surface.hard() {
            let texture = format!("decal_{}.png", surface.debris());
            if config.content_root.join(&texture).is_file() {
                let material = feedback
                    .decals
                    .entry(surface)
                    .or_insert_with(|| {
                        decals.add(ForwardDecalMaterial {
                            base: StandardMaterial {
                                base_color_texture: Some(assets.load(texture)),
                                alpha_mode: AlphaMode::Blend,
                                perceptual_roughness: 1.,
                                reflectance: 0.,
                                depth_bias: 1.,
                                ..default()
                            },
                            extension: ForwardDecalMaterialExt {
                                depth_fade_factor: 0.3,
                            },
                        })
                    })
                    .clone();
                let normal = normal.try_normalize().unwrap_or(Vec3::Y);
                let tangent = if normal.dot(Vec3::Y).abs() < 0.95 {
                    Vec3::Y
                } else {
                    Vec3::X
                };
                let right = normal.cross(tangent).normalize();
                let transform = Transform {
                    translation: point + normal * 0.06,
                    rotation: Quat::from_mat3(&Mat3::from_cols(right, normal, right.cross(normal))),
                    scale: Vec3::new(0.4, 0.3, 0.4),
                };
                let mut entity = commands.spawn((
                    Name::new("Original projected bullet hole"),
                    ForwardDecal,
                    MeshMaterial3d(material),
                    Hole { age: 0. },
                    ImpactTransient,
                    transform,
                ));
                if let Some((parent, pose)) = parent {
                    let pose = poses.current_pose(parent).unwrap_or(pose);
                    entity.insert((
                        ChildOf(parent),
                        GlobalTransform::from(transform).reparented_to(&pose),
                    ));
                }
            }
        }
        if let Some(path) = feedback.sound(surface, &config, seed) {
            sound(&mut commands, &assets, &settings, &path, point);
        }
    }
}
#[cfg(test)]
mod tests;
