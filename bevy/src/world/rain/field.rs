//! The fixed source rain/snow pools and velocity-axis camera billboards.
use super::{RainState, roof::RoofMap};
use crate::{
    particles::random,
    player::{PlayerCamera, WorldSessionEpoch},
    ui::UiState,
    world::{WorldFoliage, WorldResource},
};
use bevy::{
    asset::{RenderAssetUsages, embedded_asset},
    camera::visibility::NoFrustumCulling,
    light::NotShadowCaster,
    mesh::Indices,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::{AsBindGroup, PrimitiveTopology},
    shader::ShaderRef,
};
use bevy_rapier3d::prelude::Collider;
use std::collections::VecDeque;

pub struct FieldPlugin;
impl Plugin for FieldPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "streak.wesl");
        app.init_resource::<Field>()
            .add_plugins(MaterialPlugin::<StreakMaterial>::default())
            .add_systems(Startup, setup)
            .add_systems(
                PostUpdate,
                update.after(bevy::transform::TransformSystems::Propagate),
            );
    }
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
struct StreakExtension {
    #[uniform(100)]
    mode: Vec4,
}
impl MaterialExtension for StreakExtension {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/rain/streak.wesl".into()
    }
}
type StreakMaterial = ExtendedMaterial<StandardMaterial, StreakExtension>;
#[derive(Component)]
struct RainMesh {
    snow: bool,
}
#[derive(Clone)]
struct Drop {
    position: Vec3,
    velocity: Vec3,
    age: f32,
    scale: f32,
    angle: f32,
    spin: f32,
}
struct Pool {
    drops: Vec<Drop>,
    random: u64,
    accumulator: f32,
    warm: bool,
    snow: bool,
}
impl Pool {
    fn new(snow: bool) -> Self {
        Self {
            drops: vec![],
            random: if snow { 53129 } else { 92371 },
            accumulator: 0.,
            warm: false,
            snow,
        }
    }
    fn count(&self) -> usize {
        if self.snow { 900 } else { 6500 }
    }
    fn life(&self) -> f32 {
        if self.snow { 9. } else { 1.4 }
    }
    fn make(&mut self, origin: Vec3) -> Drop {
        let spread = if self.snow { 25_f32 } else { 3_f32 }.to_radians();
        let angle_xz = (random(&mut self.random) * 2. - 1.) * spread;
        let angle_yz = (random(&mut self.random) * 2. - 1.) * spread;
        let direction = if self.snow {
            Vec3::NEG_Y
        } else {
            Vec3::new(0.12, -1., 0.).normalize()
        };
        let binormal = Vec3::Y.cross(direction).try_normalize().unwrap_or(Vec3::Z);
        let normal = binormal.cross(direction);
        let launch = binormal * (angle_xz.sin() * angle_yz.cos())
            + normal * angle_yz.sin()
            + direction * (angle_xz.cos() * angle_yz.cos());
        let position = origin
            + Vec3::new(
                (random(&mut self.random) * 2. - 1.) * 16.,
                (random(&mut self.random) * 2. - 1.) * 2.,
                (random(&mut self.random) * 2. - 1.) * 16.,
            );
        Drop {
            position,
            velocity: launch
                * if self.snow {
                    0.3 + 0.6 * random(&mut self.random)
                } else {
                    10. + 4. * random(&mut self.random)
                },
            age: 0.,
            scale: if self.snow {
                0.6 + 1.2 * random(&mut self.random)
            } else {
                0.8 + 0.7 * random(&mut self.random)
            },
            angle: 0.,
            spin: if self.snow {
                (-40. + 80. * random(&mut self.random)).to_radians()
            } else {
                0.
            },
        }
    }
    fn restart(&mut self, origin: Vec3, gravity: Vec3) {
        self.drops.clear();
        self.accumulator = 0.;
        self.warm = true;
        // Godot's Preprocess distributes a stable pool over its lifetime before
        // the first visible frame. Preserve the authored source randomness.
        for i in 0..self.count() {
            let jitter = if self.snow { 0.9 } else { 0.7 };
            let age =
                (i as f32 + random(&mut self.random) * jitter) / self.count() as f32 * self.life();
            if self.snow && age > 8. {
                continue;
            }
            let mut drop = self.make(origin);
            let mut remaining = age;
            while remaining > 0. {
                let step = remaining.min(1. / 30.);
                advance(&mut drop, step, gravity);
                remaining -= step;
            }
            self.drops.push(drop);
        }
    }
    fn step(&mut self, dt: f32, origin: Vec3, gravity: Vec3, emitting: bool) {
        if emitting && !self.warm {
            self.restart(origin, gravity);
        }
        for drop in &mut self.drops {
            advance(drop, dt, gravity);
        }
        let life = self.life();
        self.drops.retain(|p| p.age < life);
        if !emitting {
            return;
        }
        let period = life / self.count() as f32;
        let total = self.accumulator + dt;
        let count = (total / period).floor() as usize;
        for i in 0..count {
            let mut drop = self.make(origin);
            advance(
                &mut drop,
                (total - (i + 1) as f32 * period).max(0.),
                gravity,
            );
            self.drops.push(drop);
        }
        self.accumulator = total - count as f32 * period;
    }
}
fn advance(drop: &mut Drop, dt: f32, gravity: Vec3) {
    drop.velocity += gravity * dt;
    drop.position += drop.velocity * dt;
    drop.age += dt;
    drop.angle += drop.spin * dt;
}
#[derive(Resource)]
pub(super) struct Field {
    rain: Pool,
    snow: Pool,
    epoch: u64,
    last: Option<Vec3>,
    velocity: Vec3,
    elapsed: f32,
    cooldown: f32,
    trail: VecDeque<(f32, Vec3)>,
    canopy: Vec4,
    canopy_poll: f32,
}
impl Default for Field {
    fn default() -> Self {
        Self {
            rain: Pool::new(false),
            snow: Pool::new(true),
            epoch: u64::MAX,
            last: None,
            velocity: Vec3::ZERO,
            elapsed: 0.,
            cooldown: 0.,
            trail: default(),
            canopy: Vec4::ZERO,
            canopy_poll: 0.,
        }
    }
}
impl Field {
    pub fn shift(&mut self, shift: Vec3) {
        for pool in [&mut self.rain, &mut self.snow] {
            for drop in &mut pool.drops {
                drop.position -= shift;
            }
        }
        if let Some(last) = &mut self.last {
            *last -= shift;
        }
        for (_, at) in &mut self.trail {
            *at -= shift;
        }
        self.canopy.x -= shift.x;
        self.canopy.y -= shift.z;
    }
    fn follow(&mut self, camera: Vec3, dt: f32) -> (Vec3, bool) {
        self.elapsed += dt;
        self.cooldown -= dt;
        if let Some(last) = self.last {
            if dt > 0. {
                let mut v = (camera - last) / dt;
                if v.length_squared() > 200. * 200. {
                    v = Vec3::ZERO;
                    self.velocity = Vec3::ZERO;
                }
                self.velocity = self.velocity.lerp(v, (dt / 0.6).min(1.));
            }
        }
        self.last = Some(camera);
        let mut lead = self.velocity * 0.45;
        lead.y = 0.;
        lead = lead.clamp_length_max(10.);
        self.trail.push_back((self.elapsed, camera));
        while self
            .trail
            .front()
            .is_some_and(|(t, _)| self.elapsed - t > 0.25)
        {
            self.trail.pop_front();
        }
        let restart = self.cooldown <= 0.
            && self
                .trail
                .front()
                .is_some_and(|(_, p)| camera.distance(*p) > 10.);
        if restart {
            self.cooldown = 0.4;
        }
        (camera + Vec3::Y * 10. + lead, restart)
    }
}
fn setup(mut commands: Commands, mut materials: ResMut<Assets<StreakMaterial>>) {
    for snow in [false, true] {
        let material = materials.add(ExtendedMaterial {
            base: StandardMaterial {
                unlit: true,
                alpha_mode: AlphaMode::Blend,
                cull_mode: None,
                double_sided: true,
                ..default()
            },
            extension: StreakExtension {
                mode: Vec4::new(if snow { 1. } else { 0. }, 0., 0., 0.),
            },
        });
        commands.spawn((
            Name::new(if snow {
                "Source snowfall"
            } else {
                "Source rain streaks"
            }),
            RainMesh { snow },
            Transform::IDENTITY,
            Visibility::Hidden,
            MeshMaterial3d(material),
            NoFrustumCulling,
            NotShadowCaster,
        ));
    }
}
fn mesh(
    positions: Vec<[f32; 3]>,
    normals: Vec<[f32; 3]>,
    uv: Vec<[f32; 2]>,
    colors: Vec<[f32; 4]>,
    indices: Vec<u32>,
) -> Mesh {
    Mesh::new(
        PrimitiveTopology::TriangleList,
        RenderAssetUsages::default(),
    )
    .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, positions)
    .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, normals)
    .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, uv)
    .with_inserted_attribute(Mesh::ATTRIBUTE_COLOR, colors)
    .with_inserted_indices(Indices::U32(indices))
}
fn smooth(a: f32, b: f32, x: f32) -> f32 {
    let t = ((x - a) / (b - a)).clamp(0., 1.);
    t * t * (3. - 2. * t)
}
fn rain_alpha(point: Vec3, camera: Vec3, roof: &RoofMap, canopy: Vec4, sea: f32) -> f32 {
    let shadow = if canopy.w > 0.001 {
        (1. - smooth(canopy.z * 0.72, canopy.z, point.xz().distance(canopy.xy()))) * canopy.w
    } else {
        0.
    };
    (1. - shadow)
        * smooth(1.5, 3.2, point.distance(camera))
        * (1. - roof.shelter(point, 0.15))
        * smooth(sea - 0.12, sea + 0.02, point.y)
}
fn update(
    mut commands: Commands,
    mut field: ResMut<Field>,
    rain: Res<RainState>,
    roof: Res<RoofMap>,
    epoch: Res<WorldSessionEpoch>,
    time: Res<Time>,
    ui: Res<UiState>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    mut query: Query<(
        Entity,
        &RainMesh,
        Option<&Mesh3d>,
        &GlobalTransform,
        &mut Visibility,
    )>,
    mut meshes: ResMut<Assets<Mesh>>,
    trees: Query<
        (&GlobalTransform, Option<&WorldResource>, &Name),
        (With<WorldFoliage>, With<Collider>),
    >,
) {
    if crate::player::simulation_paused(&ui) {
        return;
    }
    let Ok(camera) = camera.single() else {
        return;
    };
    let cp = camera.translation();
    if field.epoch != epoch.0 {
        *field = Field {
            epoch: epoch.0,
            ..default()
        };
    }
    let dt = time.delta_secs();
    let (origin, restart) = field.follow(cp, dt);
    field.canopy_poll -= dt;
    if field.canopy_poll <= 0. {
        field.canopy_poll = 0.25;
        field.canopy = Vec4::ZERO;
        // RainMaterialAudio queries the widest (pine) foliage radius. The
        // nearest tree then chooses its own canopy shadow and audio radius.
        let mut closest = 28. * 28.;
        for (pose, resource, name) in &trees {
            let name = resource.map_or(name.as_str(), |r| r.name.as_str());
            if !["Pine", "Birch", "Maple", "Redwood"]
                .iter()
                .any(|n| name.contains(n))
            {
                continue;
            }
            let d = pose.translation().distance_squared(cp);
            if d < closest {
                closest = d;
                let p = pose.translation();
                field.canopy = Vec4::new(p.x, p.z, if name.contains("Pine") { 6. } else { 4. }, 1.);
            }
        }
    }
    let active = rain.intensity > 0.02;
    if restart && active {
        if rain.snow {
            field.snow.restart(origin, Vec3::new(0., -1.1, 0.));
        } else {
            field.rain.restart(origin, rain.gravity);
        }
    }
    field
        .rain
        .step(dt, origin, rain.gravity, active && !rain.snow);
    field
        .snow
        .step(dt, origin, Vec3::new(0., -1.1, 0.), active && rain.snow);
    let cam = camera.compute_transform();
    let canopy = field.canopy;
    for (entity, kind, handle, pose, mut visibility) in &mut query {
        let pool = if kind.snow {
            &mut field.snow
        } else {
            &mut field.rain
        };
        if pool.drops.is_empty() || rain.intensity <= 0. {
            *visibility = Visibility::Hidden;
            continue;
        }
        *visibility = Visibility::Inherited;
        pool.drops.sort_by(|a, b| {
            b.position
                .distance_squared(cp)
                .total_cmp(&a.position.distance_squared(cp))
        });
        let mut positions = Vec::with_capacity(pool.drops.len() * 4);
        let mut normals = Vec::with_capacity(positions.capacity());
        let mut uvs = Vec::with_capacity(positions.capacity());
        let mut colors = Vec::with_capacity(positions.capacity());
        let mut indices = Vec::with_capacity(pool.drops.len() * 6);
        let inverse = pose.to_matrix().inverse();
        let tint = if kind.snow {
            Vec3::ONE
        } else {
            LinearRgba::from(Color::srgb(0.8, 0.86, 0.96))
                .to_vec4()
                .xyz()
                * (0.18 + 0.82 * rain.daylight)
        };
        for drop in &pool.drops {
            let (axis, right, size) = if kind.snow {
                let q = cam.rotation * Quat::from_rotation_z(drop.angle);
                (q * Vec3::Y, q * Vec3::X, Vec2::splat(0.055))
            } else {
                let axis = drop.velocity.try_normalize().unwrap_or(Vec3::NEG_Y);
                let right = axis
                    .cross(cp - drop.position)
                    .try_normalize()
                    .unwrap_or(Vec3::X);
                (axis, right, Vec2::new(0.014, 0.62))
            };
            let normal = right.cross(axis);
            let start = positions.len() as u32;
            for (x, y, uv) in [
                (-0.5, -0.5, [0., 1.]),
                (0.5, -0.5, [1., 1.]),
                (0.5, 0.5, [1., 0.]),
                (-0.5, 0.5, [0., 0.]),
            ] {
                let point = drop.position + (right * x * size.x + axis * y * size.y) * drop.scale;
                let alpha = if kind.snow {
                    0.85 * rain.intensity
                } else {
                    0.14 * rain.intensity * rain_alpha(point, cp, &roof, canopy, rain.sea_level)
                };
                positions.push(inverse.transform_point3(point).to_array());
                normals.push(inverse.transform_vector3(normal).to_array());
                uvs.push(uv);
                colors.push(tint.extend(alpha).to_array());
            }
            indices.extend([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        let output = mesh(positions, normals, uvs, colors, indices);
        if let Some(handle) = handle {
            if let Some(mut target) = meshes.get_mut(&handle.0) {
                *target = output;
            }
        } else {
            commands.entity(entity).insert(Mesh3d(meshes.add(output)));
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_pool_counts_and_camera_jump_restart_are_bounded() {
        let mut pool = Pool::new(false);
        pool.restart(Vec3::Y * 10., Vec3::new(5., -22., 0.));
        assert_eq!(pool.drops.len(), 6500);
        for _ in 0..60 {
            pool.step(1. / 60., Vec3::Y * 10., Vec3::new(5., -22., 0.), true);
        }
        assert!(pool.drops.len().abs_diff(6500) < 5);
        let mut field = Field::default();
        field.follow(Vec3::ZERO, 0.02);
        assert!(field.follow(Vec3::X * 20., 0.02).1);
        assert!(!field.follow(Vec3::X * 40., 0.02).1);
    }
    #[test]
    fn origin_shift_moves_drops_and_camera_trail_once_without_changing_velocity() {
        let mut field = Field::default();
        field.rain.restart(Vec3::X * 2000., Vec3::new(5., -22., 0.));
        field.follow(Vec3::X * 2000., 0.02);
        let before = field.rain.drops[0].clone();
        field.shift(Vec3::X * 1024.);
        assert_eq!(
            field.rain.drops[0].position,
            before.position - Vec3::X * 1024.
        );
        assert_eq!(field.rain.drops[0].velocity, before.velocity);
        assert!(!field.follow(Vec3::X * 976., 0.02).1);
    }
    #[test]
    fn rain_stops_at_water_and_lens_while_snow_keeps_original_square_billboard() {
        let roof = RoofMap::default();
        assert_eq!(
            rain_alpha(Vec3::ZERO, Vec3::Y * 8., &roof, Vec4::ZERO, 1.),
            0.
        );
        assert_eq!(
            rain_alpha(Vec3::Y * 8., Vec3::Y * 8., &roof, Vec4::ZERO, 1.),
            0.
        );
        assert!(rain_alpha(Vec3::Y * 8., Vec3::ZERO, &roof, Vec4::ZERO, 1.) > 0.99);
        assert_eq!(Pool::new(true).count(), 900);
        assert_eq!(Pool::new(true).life(), 9.);
    }
    #[test]
    fn dry_weather_uploads_no_empty_mesh_and_snow_does_not_reenable_rain() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
            .init_asset::<Mesh>()
            .init_asset::<StreakMaterial>()
            .init_resource::<Field>()
            .init_resource::<RainState>()
            .init_resource::<RoofMap>()
            .init_resource::<WorldSessionEpoch>()
            .insert_resource(UiState {
                screen: crate::ui::Screen::Playing,
                ..default()
            })
            .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
                std::time::Duration::from_millis(20),
            ))
            .add_systems(Startup, setup)
            .add_systems(
                PostUpdate,
                update.after(bevy::transform::TransformSystems::Propagate),
            );
        app.world_mut().spawn((PlayerCamera, Transform::IDENTITY));
        for _ in 0..5 {
            app.update();
        }
        assert!(app.world().resource::<Assets<Mesh>>().is_empty());
        {
            let mut state = app.world_mut().resource_mut::<RainState>();
            state.intensity = 1.;
            state.snow = true;
        }
        for _ in 0..5 {
            app.update();
        }
        assert!(
            app.world().resource::<Field>().rain.drops.is_empty(),
            "snow's switch must not be overwritten by intensity, as Godot HubProcess did"
        );
        assert!(app.world().resource::<Field>().snow.drops.len() > 700);
        assert_eq!(app.world().resource::<Assets<Mesh>>().len(), 1);
        app.world_mut().resource_mut::<RainState>().snow = false;
        app.update();
        assert!(app.world().resource::<Field>().rain.drops.len() > 6400);
        app.world_mut().resource_mut::<RainState>().intensity = 0.;
        for _ in 0..5 {
            app.update();
        }
        let mut query = app
            .world_mut()
            .query_filtered::<&Visibility, With<RainMesh>>();
        assert!(query.iter(app.world()).all(|v| *v == Visibility::Hidden));
        for (_, mesh) in app.world().resource::<Assets<Mesh>>().iter() {
            assert!(mesh.count_vertices() > 0);
        }
    }
}
