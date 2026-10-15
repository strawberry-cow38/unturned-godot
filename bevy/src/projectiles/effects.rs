//! Source ThrowableFx and PlayerController blast presentation. Events and
//! projectile replicas are accepted facts; these effects never apply damage.
use crate::{
    entities::{CoreEvent, ItemCatalog},
    particles::{self, ParticleAssets, Spec},
    player::WorldSessionEpoch,
    ui::UiState,
};
use bevy::prelude::*;

pub struct ProjectileEffectsPlugin;
impl Plugin for ProjectileEffectsPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(
            Update,
            (
                reset_session,
                events,
                flare_replicas,
                resolve_palette,
                age_effects,
            )
                .chain()
                .after(crate::entities::CollectInteractions),
        );
    }
}
#[derive(Component)]
struct EffectRoot {
    age: f32,
    duration: f32,
}
#[derive(Component)]
struct PendingPalette {
    texture: Handle<Image>,
    kind: EffectKind,
}
enum EffectKind {
    Smoke { radius: f32, seconds: f32 },
    Flare { seconds: f32 },
}
#[derive(Component)]
struct FlareStarted;
#[derive(Component)]
struct Flare {
    age: f32,
    duration: f32,
    seed: u64,
    phase: f32,
    lights: [Entity; 2],
    emitters: [Entity; 2],
    core: Handle<StandardMaterial>,
    tint: Color,
}
#[derive(Component)]
struct Flash(f32);
// Match the native daylight conversion (one source sun-energy unit = 18klux).
// Bevy points are lumens: 4pi converts intensity at one metre to total flux.
pub(crate) const LIGHT_UNIT: f32 = 18000. * 4. * std::f32::consts::PI;
fn color(v: Vec3, alpha: f32) -> Vec4 {
    Color::srgba(v.x, v.y, v.z, alpha).to_linear().to_vec4()
}
pub fn throwable_kind(value: &serde_json::Value) -> u64 {
    match value.as_str() {
        Some("Smoke") => 1,
        Some("Flare") => 2,
        _ => value.as_u64().unwrap_or(0),
    }
}
fn reset_session(
    mut commands: Commands,
    epoch: Res<WorldSessionEpoch>,
    mut previous: Local<u64>,
    roots: Query<Entity, With<EffectRoot>>,
) {
    if *previous == epoch.0 {
        return;
    }
    for entity in &roots {
        commands.entity(entity).despawn();
    }
    *previous = epoch.0;
}
fn events(
    mut commands: Commands,
    mut events: MessageReader<CoreEvent>,
    catalog: Res<ItemCatalog>,
    assets: Res<AssetServer>,
    particles: Res<ParticleAssets>,
    mut meshes: ResMut<Assets<Mesh>>,
) {
    for event in events.read().filter(|e| e.kind == "grenadeExploded") {
        let Some(at) = crate::player::vector(&event.data["pos"]) else {
            continue;
        };
        let id = event.data["itemId"].as_u64().unwrap_or(254) as u16;
        let def = catalog.items.get(&id).map(|i| &i.throwable);
        let kind = def.map_or(0, |d| throwable_kind(&d["kind"]));
        info!("Accepted throwable FX item={id} kind={kind} at={at}");
        match kind {
            1 => {
                let radius = def.and_then(|d| d["radius"].as_f64()).unwrap_or(0.) as f32;
                let seconds = def.and_then(|d| d["effectSeconds"].as_f64()).unwrap_or(0.) as f32;
                if radius <= 0. || seconds <= 0. {
                    continue;
                }
                commands.spawn((
                    Name::new("Authoritative smoke cloud"),
                    Transform::from_translation(at + Vec3::Y * 0.35),
                    Visibility::default(),
                    EffectRoot {
                        age: 0.,
                        duration: seconds + 6.5,
                    },
                    PendingPalette {
                        texture: assets.load(format!("items/{id}.png")),
                        kind: EffectKind::Smoke { radius, seconds },
                    },
                ));
            }
            2 => {}
            _ => {
                blast(
                    &mut commands,
                    &mut meshes,
                    &particles,
                    at,
                    event.data["radius"].as_f64().unwrap_or(8.) as f32,
                    event.sequence,
                );
            }
        }
    }
}
fn flare_replicas(
    mut commands: Commands,
    replicas: Query<(Entity, &super::ProjectileVisual), Without<FlareStarted>>,
    catalog: Res<ItemCatalog>,
    assets: Res<AssetServer>,
) {
    for (entity, projectile) in &replicas {
        if projectile.kind != 2 {
            continue;
        }
        let Some(item) = catalog.items.get(&projectile.item) else {
            continue;
        };
        let seconds = item.throwable["effectSeconds"].as_f64().unwrap_or(0.) as f32;
        if seconds <= 0. {
            continue;
        }
        commands.entity(entity).insert((
            FlareStarted,
            PendingPalette {
                texture: assets.load(format!("items/{}.png", projectile.item)),
                kind: EffectKind::Flare { seconds },
            },
        ));
    }
}
fn resolve_palette(
    mut commands: Commands,
    pending: Query<(Entity, &PendingPalette, Option<&super::ProjectileVisual>)>,
    images: Res<Assets<Image>>,
    particles: Res<ParticleAssets>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    for (entity, palette, projectile) in &pending {
        let Some(image) = images.get(&palette.texture) else {
            continue;
        };
        // WorldItem.PaletteColor reads the first pixel, including yellow's unusual cap.
        let tint = image
            .get_color_at(0, 0)
            .unwrap_or(Color::srgb(0.8, 0.8, 0.8));
        let rgb = tint.to_srgba().to_vec4().truncate();
        info!("Resolved throwable palette entity={entity:?} rgb={rgb}");
        let seed = entity.to_bits();
        match palette.kind {
            EffectKind::Smoke { radius, seconds } => {
                particles::spawn(
                    &mut commands,
                    &mut meshes,
                    &particles,
                    Spec {
                        amount: 220,
                        life: 6.5,
                        duration: seconds,
                        radius: radius * 0.55,
                        spread: 60.,
                        speed: Vec2::new(0.35, 1.4),
                        gravity: Vec3::Y * 0.35,
                        damping: Vec2::new(0.3, 0.8),
                        size: Vec2::new(radius * 0.5, radius * 0.95),
                        spin: Vec2::new(-12., 12.),
                        lit: true,
                        colors: vec![
                            (0., color(rgb, 0.)),
                            (0.08, color(rgb, 0.88)),
                            (0.7, color(rgb, 0.72)),
                            (1., color(rgb, 0.)),
                        ],
                        growth: vec![(0., 0.35), (1., 1.)],
                        ..default()
                    },
                    Vec3::ZERO,
                    Some(entity),
                    seed,
                );
            }
            EffectKind::Flare { seconds } => {
                let seconds = projectile
                    .and_then(|p| p.remaining_seconds)
                    .unwrap_or(seconds);
                let hot = rgb.lerp(Vec3::ONE, 0.5);
                let sparks = particles::spawn(
                    &mut commands,
                    &mut meshes,
                    &particles,
                    Spec {
                        amount: 90,
                        life: 0.85,
                        duration: f32::INFINITY,
                        spread: 42.,
                        speed: Vec2::new(1.6, 4.2),
                        gravity: Vec3::Y * -7.5,
                        damping: Vec2::new(0.5, 1.5),
                        size: Vec2::new(0.05, 0.12),
                        additive: true,
                        colors: vec![
                            (0., color(Vec3::new(1., 1., 0.92), 1.)),
                            (0.3, color(hot, 0.95)),
                            (1., color(rgb, 0.)),
                        ],
                        ..default()
                    },
                    Vec3::new(0., 0.06, -0.274),
                    Some(entity),
                    seed,
                );
                let plume = particles::spawn(
                    &mut commands,
                    &mut meshes,
                    &particles,
                    Spec {
                        amount: 22,
                        life: 3.2,
                        duration: f32::INFINITY,
                        spread: 22.,
                        speed: Vec2::new(0.6, 1.5),
                        gravity: Vec3::Y * 0.5,
                        damping: Vec2::new(0.4, 1.),
                        size: Vec2::new(0.35, 0.9),
                        spin: Vec2::new(-20., 20.),
                        colors: vec![
                            (0., color(rgb, 0.)),
                            (0.15, color(rgb, 0.42)),
                            (1., color(rgb, 0.)),
                        ],
                        ..default()
                    },
                    Vec3::new(0., 0.1, -0.274),
                    Some(entity),
                    seed ^ 57,
                );
                let lights = [(0.45, 22., 9., 0.35), (0.9, 46., 4.05, 0.15)].map(
                    |(y, range, energy, white)| {
                        let c = rgb.lerp(Vec3::ONE, white);
                        commands
                            .spawn((
                                PointLight {
                                    color: Color::srgb(c.x, c.y, c.z),
                                    intensity: energy * LIGHT_UNIT,
                                    range,
                                    shadow_maps_enabled: false,
                                    ..default()
                                },
                                Transform::from_xyz(0., y, -0.274),
                                ChildOf(entity),
                            ))
                            .id()
                    },
                );
                let c = rgb.lerp(Vec3::ONE, 0.6);
                let tint = Color::srgb(c.x, c.y, c.z);
                let core = materials.add(StandardMaterial {
                    base_color: tint,
                    unlit: true,
                    alpha_mode: AlphaMode::Add,
                    ..default()
                });
                commands.spawn((
                    Mesh3d(meshes.add(Sphere::new(0.075).mesh().uv(12, 8))),
                    MeshMaterial3d(core.clone()),
                    Transform::from_xyz(0., 0.06, -0.274),
                    ChildOf(entity),
                ));
                commands.entity(entity).insert(Flare {
                    age: 0.,
                    duration: seconds,
                    seed,
                    phase: (seed % 10000) as f32 * 0.01,
                    lights,
                    emitters: [sparks, plume],
                    core,
                    tint,
                });
            }
        }
        commands.entity(entity).remove::<PendingPalette>();
    }
}
/// Original fireball and two dust columns, also usable by accepted vehicle blasts.
pub fn blast(
    commands: &mut Commands,
    meshes: &mut Assets<Mesh>,
    assets: &ParticleAssets,
    at: Vec3,
    radius: f32,
    seed: u64,
) -> Entity {
    let k = (radius / 9.).clamp(0.6, 2.2);
    let root = commands
        .spawn((
            Name::new("Accepted blast FX"),
            Transform::from_translation(at),
            Visibility::default(),
            EffectRoot {
                age: 0.,
                duration: 3.3,
            },
        ))
        .id();
    commands.spawn((
        Flash(0.),
        PointLight {
            color: Color::srgb(1., 0.72, 0.35),
            intensity: 14. * LIGHT_UNIT,
            range: 14. * k + 8.,
            shadow_maps_enabled: false,
            ..default()
        },
        Transform::from_xyz(0., 0.8, 0.),
        ChildOf(root),
    ));
    particles::spawn(
        commands,
        meshes,
        assets,
        Spec {
            amount: 28,
            life: 0.55,
            one_shot: true,
            spread: 180.,
            speed: Vec2::new(4. * k, 11. * k),
            gravity: Vec3::Y * 2.,
            damping: Vec2::splat(6.),
            size: Vec2::new(1.6 * k, 3.2 * k),
            additive: true,
            colors: vec![
                (0., color(Vec3::new(1., 0.85, 0.45), 1.)),
                (0.35, color(Vec3::new(1., 0.42, 0.08), 0.9)),
                (1., color(Vec3::new(0.35, 0.05, 0.), 0.)),
            ],
            growth: vec![(0., 0.5), (0.3, 1.), (1., 0.7)],
            ..default()
        },
        Vec3::Y * 0.5,
        Some(root),
        seed,
    );
    for (n, (height, size, amount, life, tint)) in [
        (0.6, 2.2 * k, 36, 3.2, Vec3::new(0.32, 0.30, 0.27)),
        (0.3, 1.6 * k, 24, 1.6, Vec3::new(0.62, 0.56, 0.46)),
    ]
    .into_iter()
    .enumerate()
    {
        particles::spawn(
            commands,
            meshes,
            assets,
            Spec {
                amount,
                life,
                one_shot: true,
                spread: 35.,
                speed: Vec2::new(3., 7.),
                gravity: Vec3::Y * 0.6,
                damping: Vec2::splat(3.),
                size: Vec2::new(size, size * 1.8),
                colors: vec![(0., color(tint, 0.85)), (1., color(tint, 0.))],
                growth: vec![(0., 0.55), (1., 1.)],
                ..default()
            },
            Vec3::Y * height,
            Some(root),
            seed + n as u64 + 1,
        );
    }
    root
}
fn age_effects(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    mut roots: Query<(Entity, &mut EffectRoot)>,
    mut flares: Query<(&mut Flare, &super::ProjectileVisual)>,
    mut flash: Query<(Entity, &mut Flash)>,
    mut lights: Query<&mut PointLight>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut emitters: Query<&mut particles::Emitter>,
) {
    if crate::player::simulation_paused(&ui) {
        return;
    }
    let dt = time.delta_secs();
    for (entity, mut root) in &mut roots {
        root.age += dt;
        if root.age >= root.duration {
            commands.entity(entity).despawn();
        }
    }
    for (entity, mut f) in &mut flash {
        f.0 += dt;
        if f.0 >= 0.28 {
            commands.entity(entity).despawn();
        } else if f.0 >= 0.08 {
            if let Ok(mut light) = lights.get_mut(entity) {
                light.intensity = 5. * LIGHT_UNIT;
            }
        }
    }
    for (mut f, projectile) in &mut flares {
        f.age += dt;
        let remaining = projectile.remaining_seconds.unwrap_or(f.duration - f.age);
        let fade = (remaining / 4.).clamp(0., 1.);
        let phase = f.age + f.phase;
        if fade < 0.35 {
            for emitter in f.emitters {
                if let Ok(mut emitter) = emitters.get_mut(emitter) {
                    emitter.stop();
                }
            }
        }
        let flicker = 0.82
            + 0.1 * (phase * 11.3).sin()
            + 0.06 * (phase * 27.7 + 1.7).sin()
            + 0.06 * (particles::random(&mut f.seed) * 2. - 1.);
        for (i, entity) in f.lights.iter().enumerate() {
            if let Ok(mut light) = lights.get_mut(*entity) {
                light.intensity = if i == 0 { 9. } else { 4.05 } * LIGHT_UNIT * flicker * fade;
                if i == 0 {
                    light.range = 22. * (0.92 + 0.08 * flicker) * fade.max(0.2);
                }
            }
        }
        if let Some(mut mat) = materials.get_mut(&f.core) {
            mat.base_color = f.tint.with_alpha(flicker.clamp(0., 1.) * fade);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn loaded_flare_uses_authority_remaining_life_instead_of_restarting_full_burn() {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .insert_resource(Assets::<StandardMaterial>::default())
            .insert_resource(UiState {
                screen: crate::ui::Screen::Playing,
                ..default()
            })
            .add_systems(Update, age_effects);
        let mut create = |remaining| {
            let lights = [
                app.world_mut().spawn(PointLight::default()).id(),
                app.world_mut().spawn(PointLight::default()).id(),
            ];
            let entity = app
                .world_mut()
                .spawn((
                    Flare {
                        age: 0.,
                        duration: 69.,
                        seed: 1,
                        phase: 0.,
                        lights,
                        emitters: [Entity::PLACEHOLDER; 2],
                        core: default(),
                        tint: Color::WHITE,
                    },
                    super::super::ProjectileVisual {
                        item: 259,
                        kind: 2,
                        remaining_seconds: Some(remaining),
                    },
                ))
                .id();
            (entity, lights[0])
        };
        let (loaded, loaded_light) = create(2.);
        let (_, fresh_light) = create(60.);
        app.update();
        let ratio = app
            .world()
            .get::<PointLight>(loaded_light)
            .unwrap()
            .intensity
            / app
                .world()
                .get::<PointLight>(fresh_light)
                .unwrap()
                .intensity;
        assert!((ratio - 0.5).abs() < 0.0001);
        app.world_mut()
            .get_mut::<super::super::ProjectileVisual>(loaded)
            .unwrap()
            .remaining_seconds = Some(1.);
        app.update();
        let ratio = app
            .world()
            .get::<PointLight>(loaded_light)
            .unwrap()
            .intensity
            / app
                .world()
                .get::<PointLight>(fresh_light)
                .unwrap()
                .intensity;
        assert!((ratio - 0.25).abs() < 0.0001);
    }
}
