//! RainRoofMap.cs: budgeted static roof heights, median filter and cached tiles.
use super::super::{WorldObject, terrain::TerrainSurface};
use crate::player::{PlayerCamera, WorldSessionEpoch};
use bevy::{
    asset::RenderAssetUsages,
    prelude::*,
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};
use bevy_rapier3d::prelude::*;
use std::collections::HashMap;

pub const HALF: f32 = 32.;
pub const RES: usize = 128;
pub const CELL: f32 = 0.5;
pub const NO_HIT: f32 = -1e6;
const TILE: i32 = 32;
const RAYS: usize = 128;
const SCANS: usize = 1024;

#[derive(Clone, Copy, PartialEq)]
struct Bounds {
    min: Vec3,
    max: Vec3,
}
#[derive(Resource)]
pub struct RoofMap {
    tiles: HashMap<IVec2, Vec<f32>>,
    pending: Vec<(Vec3, f32, u8)>,
    bounds: HashMap<Entity, Bounds>,
    origin: IVec2,
    scan: usize,
    evict_tick: usize,
    dirty: bool,
    epoch: u64,
    pub texture: Handle<Image>,
    pub rect: Vec4,
    pub window: Vec<f32>,
    pub casts: u64,
}
impl Default for RoofMap {
    fn default() -> Self {
        Self {
            tiles: default(),
            pending: default(),
            bounds: default(),
            origin: IVec2::splat(i32::MIN),
            scan: 0,
            evict_tick: 0,
            dirty: false,
            epoch: u64::MAX,
            texture: default(),
            rect: Vec4::ZERO,
            window: vec![NO_HIT; RES * RES],
            casts: 0,
        }
    }
}
impl RoofMap {
    fn get(&self, cell: IVec2) -> f32 {
        let tile = IVec2::new(cell.x.div_euclid(TILE), cell.y.div_euclid(TILE));
        self.tiles.get(&tile).map_or(f32::NAN, |t| {
            t[(cell.y.rem_euclid(TILE) * TILE + cell.x.rem_euclid(TILE)) as usize]
        })
    }
    fn set(&mut self, cell: IVec2, value: f32, create: bool) {
        let tile = IVec2::new(cell.x.div_euclid(TILE), cell.y.div_euclid(TILE));
        if create {
            self.tiles
                .entry(tile)
                .or_insert_with(|| vec![f32::NAN; (TILE * TILE) as usize]);
        }
        if let Some(t) = self.tiles.get_mut(&tile) {
            t[(cell.y.rem_euclid(TILE) * TILE + cell.x.rem_euclid(TILE)) as usize] = value;
        }
    }
    /// Same raw cache query used by source ambient shelter; unknown cells stay open.
    pub fn covered(&self, position: Vec3) -> bool {
        let h = self.get((position.xz() / CELL).floor().as_ivec2());
        h.is_finite() && h > position.y + 0.5
    }
    pub fn invalidate(&mut self, center: Vec3, radius: f32) {
        self.pending.push((center, radius, 2));
    }
    fn invalidate_bounds(&mut self, bounds: Bounds) {
        self.invalidate(
            (bounds.min + bounds.max) * 0.5,
            (bounds.max - bounds.min).xz().length() * 0.5,
        );
    }
    fn pending(&mut self) {
        let mut waiting = Vec::new();
        for (center, radius, ticks) in std::mem::take(&mut self.pending) {
            if ticks > 0 {
                waiting.push((center, radius, ticks - 1));
                continue;
            }
            let lo = ((center.xz() - Vec2::splat(radius)) / CELL)
                .floor()
                .as_ivec2();
            let hi = ((center.xz() + Vec2::splat(radius)) / CELL)
                .floor()
                .as_ivec2();
            // Touch only existing tiles, avoiding an enormous empty loop for terrain.
            for (key, tile) in &mut self.tiles {
                let a = lo.max(*key * TILE);
                let b = hi.min(*key * TILE + IVec2::splat(TILE - 1));
                for z in a.y..=b.y {
                    for x in a.x..=b.x {
                        tile[(z.rem_euclid(TILE) * TILE + x.rem_euclid(TILE)) as usize] = f32::NAN;
                    }
                }
            }
            self.dirty = true;
        }
        self.pending = waiting;
    }
    fn smooth(&mut self) {
        let mut raw = vec![NO_HIT; (RES + 2) * (RES + 2)];
        for z in 0..RES + 2 {
            for x in 0..RES + 2 {
                let h = self.get(self.origin + IVec2::new(x as i32 - 1, z as i32 - 1));
                raw[z * (RES + 2) + x] = if h.is_nan() { NO_HIT } else { h };
            }
        }
        for z in 0..RES {
            for x in 0..RES {
                let mut nine = [0.; 9];
                for dz in 0..3 {
                    for dx in 0..3 {
                        nine[dz * 3 + dx] = raw[(z + dz) * (RES + 2) + x + dx];
                    }
                }
                nine.sort_unstable_by(f32::total_cmp);
                self.window[z * RES + x] = nine[4];
            }
        }
        let center = (self.origin.as_vec2() + Vec2::splat(RES as f32 * 0.5)) * CELL;
        self.rect = Vec4::new(center.x, center.y, HALF, 0.);
    }
    /// Bilinear blend of four cover verdicts, never bilinear heights (phantom roofs).
    pub fn shelter(&self, position: Vec3, margin: f32) -> f32 {
        if self.rect.z <= 0. {
            return 0.;
        }
        let uv = (position.xz() - self.rect.xy()) / (2. * self.rect.z) + Vec2::splat(0.5);
        if uv.min_element() < 0. || uv.max_element() > 1. {
            return 0.;
        }
        let t = uv * RES as f32 - Vec2::splat(0.5);
        let b = t.floor().as_ivec2();
        let f = t - t.floor();
        let mut cover = 0.;
        for j in 0..2 {
            for i in 0..2 {
                let c = (b + IVec2::new(i, j)).clamp(IVec2::ZERO, IVec2::splat(RES as i32 - 1));
                if position.y < self.window[c.y as usize * RES + c.x as usize] - margin {
                    cover +=
                        if i == 0 { 1. - f.x } else { f.x } * if j == 0 { 1. - f.y } else { f.y };
                }
            }
        }
        cover
    }
    pub fn shift(&mut self, shift: Vec3) {
        let units = shift.xz() / (TILE as f32 * CELL);
        let whole = units.round();
        if (units - whole).abs().max_element() > 1e-4 {
            self.tiles.clear();
        } else {
            self.tiles = self
                .tiles
                .drain()
                .map(|(key, value)| (key - whole.as_ivec2(), value))
                .collect();
        }
        for (center, _, _) in &mut self.pending {
            *center -= shift;
        }
        for bounds in self.bounds.values_mut() {
            bounds.min -= shift;
            bounds.max -= shift;
        }
        self.origin = IVec2::splat(i32::MIN);
        self.rect = Vec4::ZERO;
        self.dirty = true;
    }
}
pub fn setup(mut roof: ResMut<RoofMap>, mut images: ResMut<Assets<Image>>) {
    roof.texture = images.add(Image::new(
        Extent3d {
            width: RES as u32,
            height: RES as u32,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        roof.window.iter().flat_map(|v| v.to_le_bytes()).collect(),
        TextureFormat::R32Float,
        RenderAssetUsages::default(),
    ));
}
pub fn update(
    mut roof: ResMut<RoofMap>,
    epoch: Res<WorldSessionEpoch>,
    camera: Query<&GlobalTransform, With<PlayerCamera>>,
    context: ReadRapierContext,
    mut images: ResMut<Assets<Image>>,
    changed: Query<
        (Entity, &Collider, &GlobalTransform, Has<ColliderDisabled>),
        Or<(
            Changed<Collider>,
            Changed<GlobalTransform>,
            Added<ColliderDisabled>,
        )>,
    >,
    bodies: Query<(
        &Collider,
        &GlobalTransform,
        Option<&WorldObject>,
        Has<TerrainSurface>,
    )>,
    hierarchy: Query<(Option<&RigidBody>, Option<&ChildOf>, Option<&WorldObject>)>,
    mut removed: RemovedComponents<Collider>,
    mut enabled: RemovedComponents<ColliderDisabled>,
) {
    let Ok(camera) = camera.single() else {
        return;
    };
    let Ok(physics) = context.single() else {
        return;
    };
    if roof.epoch != epoch.0 {
        roof.tiles.clear();
        roof.pending.clear();
        roof.bounds.clear();
        roof.origin = IVec2::splat(i32::MIN);
        roof.epoch = epoch.0;
    }
    for (entity, collider, global, disabled) in &changed {
        let mut parent = entity;
        let mut dynamic = false;
        for _ in 0..16 {
            let Ok((body, relation, _)) = hierarchy.get(parent) else {
                break;
            };
            if let Some(body) = body {
                dynamic = *body != RigidBody::Fixed;
                break;
            }
            let Some(relation) = relation else {
                break;
            };
            parent = relation.parent();
        }
        if dynamic {
            continue;
        }
        if disabled {
            if let Some(old) = roof.bounds.remove(&entity) {
                roof.invalidate_bounds(old);
            }
            continue;
        }
        let pose = global.compute_transform();
        let aabb = collider.aabb(pose.translation, pose.rotation);
        let bounds = Bounds {
            min: aabb.min.into(),
            max: aabb.max.into(),
        };
        let old = roof.bounds.insert(entity, bounds);
        if old != Some(bounds) {
            if let Some(old) = old {
                roof.invalidate_bounds(old);
            }
            roof.invalidate_bounds(bounds);
        }
    }
    for entity in removed.read() {
        if let Some(old) = roof.bounds.remove(&entity) {
            roof.invalidate_bounds(old);
        }
    }
    for entity in enabled.read() {
        if let Ok((collider, global, _, _)) = bodies.get(entity) {
            let pose = global.compute_transform();
            let aabb = collider.aabb(pose.translation, pose.rotation);
            let bounds = Bounds {
                min: aabb.min.into(),
                max: aabb.max.into(),
            };
            roof.bounds.insert(entity, bounds);
            roof.invalidate_bounds(bounds);
        }
    }
    roof.pending();
    let position = camera.translation();
    roof.evict_tick += 1;
    if roof.evict_tick >= 120 {
        roof.evict_tick = 0;
        roof.tiles.retain(|key, _| {
            ((key.as_vec2() + Vec2::splat(0.5)) * TILE as f32 * CELL - position.xz())
                .length_squared()
                <= 512. * 512.
        });
    }
    let origin = (position.xz() / CELL).floor().as_ivec2() - IVec2::splat(RES as i32 / 2);
    if origin != roof.origin {
        roof.origin = origin;
        roof.dirty = true;
    }
    let predicate = |entity: Entity, _: &bevy_rapier3d::rapier::geometry::Collider| {
        let mut parent = entity;
        for _ in 0..16 {
            let Ok((_, relation, object)) = hierarchy.get(parent) else {
                break;
            };
            if object.is_some_and(|object| lamp_fixture(&object.asset_name)) {
                return false;
            }
            let Some(relation) = relation else { break };
            parent = relation.parent();
        }
        let Ok((collider, global, object, terrain)) = bodies.get(entity) else {
            return true;
        };
        if let Some(object) = object {
            if lamp_fixture(&object.asset_name) {
                return false;
            }
        }
        if terrain {
            return true;
        }
        let pose = global.compute_transform();
        let bounds = collider.aabb(pose.translation, pose.rotation);
        let size = bounds.max - bounds.min;
        !small(size.into())
    };
    let mut rays = 0;
    for _ in 0..SCANS {
        if rays >= RAYS {
            break;
        }
        let index = roof.scan;
        roof.scan = (roof.scan + 1) % (RES * RES);
        let cell = origin + IVec2::new((index % RES) as i32, (index / RES) as i32);
        if !roof.get(cell).is_nan() {
            continue;
        }
        let point = (cell.as_vec2() + Vec2::splat(0.5)) * CELL;
        let hit = physics.cast_ray(
            Vec3::new(point.x, position.y + 120., point.y),
            Vec3::NEG_Y,
            400.,
            true,
            QueryFilter::only_fixed()
                .exclude_sensors()
                .predicate(&predicate),
        );
        let h = hit.map_or(NO_HIT, |(_, distance)| position.y + 120. - distance);
        roof.set(cell, h, true);
        roof.casts += 1;
        rays += 1;
        roof.dirty = true;
    }
    if roof.dirty {
        roof.dirty = false;
        roof.smooth();
        if let Some(mut image) = images.get_mut(&roof.texture) {
            image.data = Some(roof.window.iter().flat_map(|v| v.to_le_bytes()).collect());
        }
    }
}
fn small(size: Vec3) -> bool {
    size.y <= 1.7 || size.x * size.z <= 1.5
}
pub(super) fn lamp_fixture(name: &str) -> bool {
    name.starts_with("Streetlight")
        || name.starts_with("Street_Light")
        || name.starts_with("Traffic_Light")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn negative_tiles_rebase_without_losing_cached_height() {
        let mut roof = RoofMap::default();
        roof.set(IVec2::new(-1, -33), 14., true);
        assert!(roof.covered(Vec3::new(-0.2, 10., -16.2)));
        roof.shift(Vec3::new(1024., 0., -256.));
        assert!(roof.covered(Vec3::new(-1024.2, 10., 239.8)));
        assert!(!roof.covered(Vec3::new(-0.2, 10., -16.2)));
    }
    #[test]
    fn median_preserves_real_heights_and_blends_cover_not_height() {
        let mut roof = RoofMap::default();
        roof.origin = IVec2::ZERO;
        for z in -1..=RES as i32 {
            for x in -1..=RES as i32 {
                roof.set(IVec2::new(x, z), if x < 64 { 20. } else { 0. }, true);
            }
        }
        roof.set(IVec2::new(20, 20), 500., true);
        roof.smooth();
        assert_eq!(roof.window[20 * RES + 20], 20.);
        assert!((roof.shelter(Vec3::new(32., 10., 20.), 0.15) - 0.5).abs() < 1e-6);
        assert_eq!(roof.shelter(Vec3::new(60., 10., 20.), 0.15), 0.);
    }
    #[test]
    fn changing_cover_waits_for_physics_and_small_props_do_not_cast_roof_shadows() {
        let mut roof = RoofMap::default();
        roof.set(IVec2::ZERO, 20., true);
        roof.invalidate(Vec3::ZERO, 1.);
        roof.pending();
        roof.pending();
        assert_eq!(roof.get(IVec2::ZERO), 20.);
        roof.pending();
        assert!(roof.get(IVec2::ZERO).is_nan());
        assert!(small(Vec3::new(10., 1.7, 2.)));
        assert!(small(Vec3::new(0.5, 10., 2.)));
        assert!(!small(Vec3::new(8., 4., 8.)));
        assert!(lamp_fixture("Traffic_Light_0"));
    }
    #[test]
    fn real_roof_cache_tracks_destroyed_and_restored_cover_and_ignores_moving_bodies() {
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            AssetPlugin::default(),
            RapierPhysicsPlugin::<NoUserData>::default(),
        ))
        .init_asset::<Image>()
        .init_resource::<RoofMap>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .add_systems(Startup, setup)
        .add_systems(PostUpdate, update.after(PhysicsSet::Writeback));
        app.world_mut()
            .spawn((PlayerCamera, Transform::from_xyz(0., 1.6, 0.)));
        app.world_mut().spawn((
            RigidBody::Fixed,
            Collider::cuboid(40., 2., 40.),
            Transform::from_xyz(0., -2., 0.),
        ));
        let building = app
            .world_mut()
            .spawn((
                RigidBody::Fixed,
                Collider::cuboid(4., 2., 4.),
                Transform::from_xyz(0., 5., 0.),
            ))
            .id();
        app.world_mut().spawn((
            RigidBody::Dynamic,
            GravityScale(0.),
            Velocity::linear(Vec3::X),
            Collider::cuboid(3., 2., 3.),
            Transform::from_xyz(10., 5., 0.),
        ));
        app.world_mut().spawn((
            RigidBody::Fixed,
            Collider::cuboid(0.1, 8., 0.1),
            Transform::from_xyz(20., 8., 0.),
        ));
        let fixture = app
            .world_mut()
            .spawn((
                RigidBody::Fixed,
                WorldObject {
                    guid: "fixture".into(),
                    asset_name: "Traffic_Light_0".into(),
                    source_index: 0,
                },
                Transform::from_xyz(-12., 0., 0.),
            ))
            .id();
        app.world_mut().spawn((
            ChildOf(fixture),
            Collider::cuboid(3., 2., 3.),
            Transform::from_xyz(0., 5., 0.),
        ));
        for _ in 0..140 {
            app.update();
        }
        let roof = app.world().resource::<RoofMap>();
        assert!(roof.covered(Vec3::new(0., 1.6, 0.)));
        let stable_casts = roof.casts;
        assert!(
            !roof.covered(Vec3::new(10., 1.6, 0.)),
            "a moving vehicle must not leave a cached rain shadow"
        );
        assert!(
            !roof.covered(Vec3::new(20., 1.6, 0.)),
            "a narrow pole is not a roof"
        );
        assert!(
            !roof.covered(Vec3::new(-12., 1.6, 0.)),
            "a child collider must inherit the fixture's source roof exclusion"
        );
        for _ in 0..30 {
            app.update();
        }
        assert_eq!(
            app.world().resource::<RoofMap>().casts,
            stable_casts,
            "a moving body must not invalidate the static cache"
        );
        app.world_mut()
            .entity_mut(building)
            .insert(ColliderDisabled);
        for _ in 0..50 {
            app.update();
        }
        assert!(
            !app.world()
                .resource::<RoofMap>()
                .covered(Vec3::new(0., 1.6, 0.)),
            "destroyed cover must be recast after its physics removal"
        );
        app.world_mut()
            .entity_mut(building)
            .remove::<ColliderDisabled>();
        for _ in 0..50 {
            app.update();
        }
        assert!(
            app.world()
                .resource::<RoofMap>()
                .covered(Vec3::new(0., 1.6, 0.)),
            "restored cover must shelter again"
        );
    }
}
