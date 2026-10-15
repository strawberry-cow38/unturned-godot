//! CPU particles for the source game's smoke, flare and blast emitters. One
//! dynamic mesh per emitter keeps hundreds of lit billboards in one draw call.
use crate::{player::PlayerCamera, ui::UiState};
use bevy::{
    asset::RenderAssetUsages,
    camera::visibility::NoFrustumCulling,
    light::NotShadowCaster,
    prelude::*,
    render::render_resource::{Extent3d, PrimitiveTopology, TextureDimension, TextureFormat},
};

#[derive(Clone)]
pub struct Spec {
    pub amount: usize,
    pub life: f32,
    pub duration: f32,
    pub one_shot: bool,
    pub radius: f32,
    pub spread: f32,
    pub speed: Vec2,
    pub gravity: Vec3,
    pub damping: Vec2,
    pub size: Vec2,
    pub spin: Vec2,
    pub colors: Vec<(f32, Vec4)>,
    pub growth: Vec<(f32, f32)>,
    pub lit: bool,
    pub additive: bool,
    /// Source texture/material override, e.g. vehicle smoke and flame atlases.
    pub material: Option<Handle<StandardMaterial>>,
    /// World-space launch direction; smoke can remain world-up while its parent tumbles.
    pub direction: Vec3,
    pub angle: Vec2,
    pub atlas: UVec2,
    /// Godot CPUParticles3D expresses animation speed in atlas cycles per lifetime.
    pub animation_speed: Vec2,
    pub animation_offset: Vec2,
    pub animation_loop: bool,
}
impl Default for Spec {
    fn default() -> Self {
        Self {
            amount: 1,
            life: 1.,
            duration: 1.,
            one_shot: false,
            radius: 0.,
            spread: 0.,
            speed: Vec2::ZERO,
            gravity: Vec3::ZERO,
            damping: Vec2::ZERO,
            size: Vec2::ONE,
            spin: Vec2::ZERO,
            colors: vec![(0., Vec4::ONE), (1., Vec4::ZERO)],
            growth: vec![(0., 1.), (1., 1.)],
            lit: false,
            additive: false,
            material: None,
            direction: Vec3::Y,
            angle: Vec2::new(0., 360.),
            atlas: UVec2::ONE,
            animation_speed: Vec2::ZERO,
            animation_offset: Vec2::ZERO,
            animation_loop: false,
        }
    }
}
#[derive(Clone)]
struct Particle {
    position: Vec3,
    velocity: Vec3,
    age: f32,
    size: f32,
    angle: f32,
    spin: f32,
    damping: f32,
    animation_speed: f32,
    animation_offset: f32,
}
#[derive(Component)]
pub struct Emitter {
    spec: Spec,
    age: f32,
    accumulator: f64,
    particles: Vec<Particle>,
    random: u64,
    initial: bool,
    opacity: f32,
}
impl Emitter {
    pub fn debug_state(&self) -> (f32, usize) {
        (self.age, self.particles.len())
    }
    /// Cease births, allowing existing particles to dissipate naturally.
    pub fn stop(&mut self) {
        self.spec.duration = self.age;
    }
    pub fn set_opacity(&mut self, opacity: f32) {
        self.opacity = opacity.clamp(0., 1.);
    }
    pub fn set_direction(&mut self, direction: Vec3) {
        self.spec.direction = direction;
    }
    pub fn set_amount(&mut self, amount: usize) {
        self.spec.amount = amount;
    }
    pub fn shift_origin(&mut self, shift: Vec3) {
        for p in &mut self.particles {
            p.position -= shift;
        }
    }
    fn advance(&mut self, dt: f32, origin: Vec3) {
        let spec = &self.spec;
        for p in &mut self.particles {
            p.advance(spec, dt);
        }
        let mut births = Vec::new();
        if spec.one_shot {
            if self.initial {
                births.resize(spec.amount, dt);
            }
        } else if spec.life > 0. && spec.amount > 0 {
            let period = spec.life as f64 / spec.amount as f64;
            let active = dt.min((spec.duration - self.age).max(0.)) as f64;
            let total = self.accumulator + active / period;
            let count = total.floor() as usize;
            for index in 0..count {
                let next = (index as f64 + 1. - self.accumulator) * period;
                births.push((dt - next as f32).max(0.));
            }
            self.accumulator = total - count as f64;
        }
        self.initial = false;
        self.age += dt;
        for elapsed in births {
            let offset =
                direction(&mut self.random, 180.) * random(&mut self.random).cbrt() * spec.radius;
            let rotation =
                Quat::from_rotation_arc(Vec3::Y, spec.direction.try_normalize().unwrap_or(Vec3::Y));
            let velocity = rotation
                * direction(&mut self.random, spec.spread)
                * between(&mut self.random, spec.speed);
            let mut p = Particle {
                position: origin + offset,
                velocity,
                age: 0.,
                size: between(&mut self.random, spec.size),
                angle: between(&mut self.random, spec.angle).to_radians(),
                spin: between(&mut self.random, spec.spin).to_radians(),
                damping: between(&mut self.random, spec.damping),
                animation_speed: between(&mut self.random, spec.animation_speed),
                animation_offset: between(&mut self.random, spec.animation_offset),
            };
            p.advance(spec, elapsed);
            self.particles.push(p);
        }
        self.particles.retain(|p| p.age < spec.life);
    }
    fn finished(&self) -> bool {
        self.particles.is_empty() && (self.spec.one_shot || self.age >= self.spec.duration)
    }
}
impl Particle {
    fn atlas_uv(&self, spec: &Spec, uv: Vec2) -> Vec2 {
        let atlas = spec.atlas.max(UVec2::ONE);
        let frames = atlas.x * atlas.y;
        let cycles = self.animation_offset + (self.age / spec.life) * self.animation_speed;
        let phase = if spec.animation_loop {
            cycles.rem_euclid(1.)
        } else {
            cycles.clamp(0., 1.)
        };
        let frame = ((phase * frames as f32).floor() as u32).min(frames - 1);
        (Vec2::new((frame % atlas.x) as f32, (frame / atlas.x) as f32) + uv) / atlas.as_vec2()
    }
    fn advance(&mut self, spec: &Spec, dt: f32) {
        self.age += dt;
        self.velocity += spec.gravity * dt;
        let speed = self.velocity.length();
        if speed > 0. {
            self.velocity *= ((speed - self.damping * dt).max(0.)) / speed;
        }
        self.position += self.velocity * dt;
        self.angle += self.spin * dt;
    }
}
#[derive(Resource)]
pub struct ParticleAssets {
    material: Handle<StandardMaterial>,
    unlit: Handle<StandardMaterial>,
    additive: Handle<StandardMaterial>,
}
pub struct ParticlePlugin;
impl Plugin for ParticlePlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Startup, setup).add_systems(
            PostUpdate,
            update.after(bevy::transform::TransformSystems::Propagate),
        );
    }
}
fn setup(
    mut commands: Commands,
    mut images: ResMut<Assets<Image>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let mut pixels = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64 {
        for x in 0..64 {
            let d =
                Vec2::new((x as f32 + 0.5) / 64. - 0.5, (y as f32 + 0.5) / 64. - 0.5).length() * 2.;
            let a = (1. - d).clamp(0., 1.);
            let a = a * a * (3. - 2. * a);
            pixels.extend([255, 255, 255, (a * 255.).round() as u8]);
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: 64,
            height: 64,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        pixels,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = bevy::image::ImageSampler::linear();
    let texture = images.add(image);
    let material = StandardMaterial {
        base_color_texture: Some(texture),
        alpha_mode: AlphaMode::Blend,
        perceptual_roughness: 1.,
        reflectance: 0.,
        ..default()
    };
    let lit = materials.add(material.clone());
    let unlit = materials.add(StandardMaterial {
        unlit: true,
        ..material.clone()
    });
    let additive = materials.add(StandardMaterial {
        unlit: true,
        alpha_mode: AlphaMode::Add,
        ..material
    });
    commands.insert_resource(ParticleAssets {
        material: lit,
        unlit,
        additive,
    });
}
pub fn spawn(
    commands: &mut Commands,
    meshes: &mut Assets<Mesh>,
    assets: &ParticleAssets,
    spec: Spec,
    at: Vec3,
    parent: Option<Entity>,
    seed: u64,
) -> Entity {
    let material = spec.material.clone().unwrap_or_else(|| {
        if spec.additive {
            assets.additive.clone()
        } else if spec.lit {
            assets.material.clone()
        } else {
            assets.unlit.clone()
        }
    });
    let mesh = Mesh::new(
        PrimitiveTopology::TriangleList,
        RenderAssetUsages::default(),
    )
    .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, Vec::<[f32; 3]>::new())
    .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, Vec::<[f32; 3]>::new())
    .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, Vec::<[f32; 2]>::new())
    .with_inserted_attribute(Mesh::ATTRIBUTE_COLOR, Vec::<[f32; 4]>::new());
    let entity = commands
        .spawn((
            Name::new("Source particle emitter"),
            Emitter {
                spec,
                age: 0.,
                accumulator: 0.,
                particles: Vec::new(),
                random: seed.max(1),
                initial: true,
                opacity: 1.,
            },
            Mesh3d(meshes.add(mesh)),
            MeshMaterial3d(material),
            Transform::from_translation(at),
            NoFrustumCulling,
            NotShadowCaster,
        ))
        .id();
    if let Some(parent) = parent {
        commands.entity(entity).insert(ChildOf(parent));
    }
    entity
}
pub(crate) fn random(seed: &mut u64) -> f32 {
    *seed ^= *seed << 13;
    *seed ^= *seed >> 7;
    *seed ^= *seed << 17;
    (*seed >> 40) as f32 / 16777216.
}
fn between(seed: &mut u64, range: Vec2) -> f32 {
    range.x + (range.y - range.x) * random(seed)
}
fn direction(seed: &mut u64, spread: f32) -> Vec3 {
    let y = 1. - random(seed) * (1. - spread.to_radians().cos());
    let a = random(seed) * std::f32::consts::TAU;
    let r = (1. - y * y).max(0.).sqrt();
    Vec3::new(a.cos() * r, y, a.sin() * r)
}
fn curve<T: Copy + std::ops::Mul<f32, Output = T> + std::ops::Add<Output = T>>(
    points: &[(f32, T)],
    t: f32,
    smooth: bool,
) -> T {
    for p in points.windows(2) {
        if t <= p[1].0 {
            let f = ((t - p[0].0) / (p[1].0 - p[0].0)).clamp(0., 1.);
            // Source Gradient colors interpolate linearly. Source Curve growth
            // points have zero default tangents, giving a smooth Hermite segment.
            let f = if smooth { f * f * (3. - 2. * f) } else { f };
            return p[0].1 * (1. - f) + p[1].1 * f;
        }
    }
    points.last().expect("source particle ramp").1
}
fn update(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    mut emitters: Query<(Entity, &mut Emitter, &GlobalTransform, &Mesh3d)>,
    mut meshes: ResMut<Assets<Mesh>>,
) {
    if crate::player::simulation_paused(&ui) {
        return;
    }
    let Ok(camera) = camera.single() else {
        return;
    };
    let dt = time.delta_secs();
    let camera_pose = camera.compute_transform();
    let right = camera_pose.rotation * Vec3::X;
    let up = camera_pose.rotation * Vec3::Y;
    let normal = camera_pose.rotation * Vec3::Z;
    for (entity, mut e, pose, mesh) in &mut emitters {
        e.advance(dt, pose.translation());
        let spec = &e.spec.clone();
        if e.finished() {
            commands.entity(entity).despawn();
            continue;
        }
        e.particles.sort_by(|a, b| {
            b.position
                .distance_squared(camera_pose.translation)
                .total_cmp(&a.position.distance_squared(camera_pose.translation))
        });
        let inv = pose.to_matrix().inverse();
        let normal = inv.transform_vector3(normal).normalize_or_zero().to_array();
        let mut positions = Vec::with_capacity(e.particles.len() * 6);
        let mut normals = Vec::with_capacity(positions.capacity());
        let mut uvs = Vec::with_capacity(positions.capacity());
        let mut colors = Vec::with_capacity(positions.capacity());
        for p in &e.particles {
            let age = p.age / spec.life;
            let mut color = curve(&spec.colors, age, false);
            color.w *= e.opacity;
            let size = p.size * curve(&spec.growth, age, true) * 0.5;
            let x = (right * p.angle.cos() + up * p.angle.sin()) * size;
            let y = (up * p.angle.cos() - right * p.angle.sin()) * size;
            let corners = [
                p.position - x - y,
                p.position + x - y,
                p.position + x + y,
                p.position - x + y,
            ];
            for i in [0, 1, 2, 0, 2, 3] {
                positions.push(inv.transform_point3(corners[i]).to_array());
                normals.push(normal);
                uvs.push(
                    p.atlas_uv(
                        spec,
                        Vec2::from_array([[0., 1.], [1., 1.], [1., 0.], [0., 0.]][i]),
                    )
                    .to_array(),
                );
                colors.push(color.to_array());
            }
        }
        if let Some(mut mesh) = meshes.get_mut(&mesh.0) {
            mesh.insert_attribute(Mesh::ATTRIBUTE_POSITION, positions);
            mesh.insert_attribute(Mesh::ATTRIBUTE_NORMAL, normals);
            mesh.insert_attribute(Mesh::ATTRIBUTE_UV_0, uvs);
            mesh.insert_attribute(Mesh::ATTRIBUTE_COLOR, colors);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_growth_uses_zero_tangent_curves_but_color_gradients_stay_linear() {
        let growth = [(0., 0.35), (1., 1.)];
        // Measured with the original Godot Curve.sample, not a native re-bake.
        assert!((curve(&growth, 0.25, true) - 0.451562494).abs() < 0.000001);
        assert!((curve(&growth, 0.75, true) - 0.8984375).abs() < 0.000001);
        assert!((curve(&growth, 0.25, false) - 0.5125).abs() < 0.000001);
    }
    fn emitter(spec: Spec) -> Emitter {
        Emitter {
            spec,
            age: 0.,
            accumulator: 0.,
            particles: vec![],
            random: 7,
            initial: true,
            opacity: 1.,
        }
    }
    #[test]
    fn source_fire_selects_one_atlas_cell_and_loops_by_lifetime_without_stretching() {
        let spec = Spec {
            atlas: UVec2::new(4, 1),
            one_shot: true,
            animation_speed: Vec2::splat(5.),
            animation_loop: true,
            ..default()
        };
        let mut e = emitter(spec);
        e.advance(0.01, Vec3::ZERO);
        let mut p = e.particles[0].clone();
        p.age = 0.075;
        assert_eq!(p.atlas_uv(&e.spec, Vec2::ZERO), Vec2::new(0.25, 0.));
        assert_eq!(p.atlas_uv(&e.spec, Vec2::ONE), Vec2::new(0.5, 1.));
        p.age = 0.275;
        assert_eq!(p.atlas_uv(&e.spec, Vec2::ZERO), Vec2::new(0.25, 0.));
    }
    #[test]
    fn continuous_birth_count_matches_the_accumulator_at_exact_frame_boundaries() {
        for dt in [0.05, 0.1, 0.2, 0.25] {
            let mut e = emitter(Spec {
                amount: 200,
                life: 10.,
                duration: 100.,
                direction: Vec3::X,
                speed: Vec2::ONE,
                ..default()
            });
            for _ in 0..(5. / dt) as usize {
                e.advance(dt, Vec3::ZERO);
            }
            assert_eq!(e.particles.len(), 100, "dt={dt}");
            assert!(
                e.particles
                    .iter()
                    .all(|p| p.velocity.x > 0.99 && p.velocity.y.abs() < 1e-6)
            );
        }
    }
    #[test]
    fn smoke_stops_emitting_then_dissipates_instead_of_popping() {
        let mut e = emitter(Spec {
            amount: 220,
            life: 6.5,
            duration: 12.,
            ..default()
        });
        for _ in 0..600 {
            e.advance(0.02, Vec3::ZERO);
        }
        assert!((218..=221).contains(&e.particles.len()));
        assert!(!e.finished());
        let particles = e.particles.len();
        e.stop();
        e.advance(3., Vec3::ZERO);
        assert!(e.particles.len() < particles);
        assert!(!e.finished());
        e.advance(3.51, Vec3::ZERO);
        assert!(e.finished());
    }
    #[test]
    fn moving_emitters_leave_smoke_behind_and_origin_shift_moves_existing_puffs() {
        let mut e = emitter(Spec {
            amount: 10,
            life: 1.,
            duration: 10.,
            ..default()
        });
        e.advance(0.21, Vec3::ZERO);
        e.advance(0.21, Vec3::X * 10.);
        assert!(e.particles.iter().any(|p| p.position.x == 0.));
        assert!(e.particles.iter().any(|p| p.position.x == 10.));
        e.shift_origin(Vec3::X * 256.);
        assert!(e.particles.iter().all(|p| p.position.x <= -246.));
    }
    #[test]
    fn blast_emits_once_and_bounds_live_particle_count_across_frame_rates() {
        let mut burst = emitter(Spec {
            amount: 28,
            life: 0.55,
            one_shot: true,
            ..default()
        });
        burst.advance(0.01, Vec3::ZERO);
        assert_eq!(burst.particles.len(), 28);
        burst.advance(0.1, Vec3::ZERO);
        assert_eq!(burst.particles.len(), 28);
        burst.advance(0.5, Vec3::ZERO);
        assert!(burst.finished());
        for dt in [0.01, 0.02, 0.1, 0.25] {
            let mut smoke = emitter(Spec {
                amount: 220,
                life: 6.5,
                duration: 30.,
                ..default()
            });
            for _ in 0..(15. / dt) as usize {
                smoke.advance(dt, Vec3::ZERO);
            }
            assert!(
                (218..=221).contains(&smoke.particles.len()),
                "dt {dt}: {}",
                smoke.particles.len()
            );
        }
    }
}
