//! Original map/content assembly. Missing retail terrain is reported explicitly.
use crate::content::{ContentCache, ContentError, ObjData, load_guid_map};
mod foliage;
pub mod interactables;
pub mod layers;
pub mod origin;
mod procedural;
mod procedural_roads;
mod roads;
mod terrain;
mod tunnels;
pub mod underwater;
mod water;
mod wires;
use bevy::{math::DVec3, prelude::*};
use bevy_rapier3d::prelude::{Collider, RigidBody};
#[cfg(test)]
use procedural::ProceduralTree;
use procedural::{ProceduralRegion, procedural_tree_name, procedural_tree_transform};
use std::{
    collections::HashMap,
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};

#[derive(Resource, Clone)]
pub struct WorldConfig {
    pub content_root: PathBuf,
    /// Unturned map folder itself, containing Landscape/Heightmaps.
    pub map_root: Option<PathBuf>,
    pub map_name: String,
    pub spawn_center: Option<Vec3>,
    pub spawn_radius: f32,
    /// Original UG_INF_CAM absolute XYZ and yaw/pitch in degrees.
    pub fixed_camera: Option<(DVec3, f32, f32)>,
    /// JSON exported from the original engine-independent InfiniteTerrain generator.
    pub procedural_path: Option<PathBuf>,
    pub foliage: bool,
}
impl Default for WorldConfig {
    fn default() -> Self {
        Self {
            content_root: Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content"),
            map_root: std::env::var_os("UG_UNTURNED_DIR")
                .map(|p| PathBuf::from(p).join("Maps/PEI")),
            map_name: "PEI".into(),
            spawn_center: None,
            spawn_radius: 180.,
            fixed_camera: std::env::var("UG_INF_CAM")
                .ok()
                .and_then(|s| parse_capture_camera(&s)),
            procedural_path: None,
            foliage: true,
        }
    }
}
#[derive(Resource, Default)]
pub struct WorldState {
    pub ready: bool,
    pub spawn: Vec3,
    /// Town/content focus used by the menu camera; gameplay uses an authored player spawn.
    pub focus: Vec3,
    pub spawn_yaw: f32,
    pub sea_level: Option<f32>,
    pub object_count: usize,
    pub foliage_count: usize,
    pub terrain_available: bool,
    pub missing_assets: Vec<String>,
    pub description: String,
}
#[derive(SystemSet, Debug, Clone, PartialEq, Eq, Hash)]
pub struct WorldReady;
pub struct WorldPlugin;
impl Plugin for WorldPlugin {
    fn build(&self, app: &mut App) {
        bevy::shader::load_shader_library!(app, "world/water_optics.wesl");
        app.add_plugins((
            foliage::FoliagePlugin,
            terrain::TerrainPlugin,
            interactables::InteractablePlugin,
            origin::OriginPlugin,
            water::WaterPlugin,
            underwater::UnderwaterPlugin,
        ))
        .init_resource::<WorldConfig>()
        .init_resource::<WorldState>()
        .init_resource::<WorldGeometry>()
        .init_resource::<WorldSourceCache>()
        .add_systems(Startup, build_world.in_set(WorldReady))
        .add_systems(
            Update,
            build_world.run_if(world_needs_reload).in_set(WorldReady),
        );
    }
}
/// Request a rebuild by changing WorldConfig, then setting WorldState.ready = false.
/// Existing world entities are retired and caches remain shared across reloads.
fn world_needs_reload(
    state: Res<WorldState>,
    geometry: Res<WorldGeometry>,
    origin: Res<origin::WorldOrigin>,
    config: Res<WorldConfig>,
    ui: Res<crate::ui::UiState>,
    players: Query<&Transform, With<crate::player::PlayerBody>>,
) -> bool {
    if !state.ready {
        return true;
    }
    if let Some((position, _, _)) = config.fixed_camera {
        return stream_due(
            origin.local(position),
            geometry.stream_anchor,
            config.spawn_radius,
        );
    }
    ui.screen != crate::ui::Screen::MainMenu
        && players
            .iter()
            .next()
            .is_some_and(|p| stream_due(p.translation, geometry.stream_anchor, config.spawn_radius))
}
fn parse_capture_camera(source: &str) -> Option<(DVec3, f32, f32)> {
    let values = origin::parse_absolute(source)?;
    (values.len() >= 3)
        .then(|| {
            (
                DVec3::new(values[0], values[1], values[2]),
                values.get(3).copied().unwrap_or(0.) as f32,
                values.get(4).copied().unwrap_or(-20.) as f32,
            )
        })
        .filter(|(_, yaw, pitch)| yaw.is_finite() && pitch.is_finite())
}
fn stream_due(player: Vec3, anchor: Vec3, radius: f32) -> bool {
    player.xz().distance(anchor.xz()) > (radius * 0.4).clamp(32., 96.)
}
#[derive(Resource, Default)]
struct WorldSourceCache {
    placements: HashMap<PathBuf, Arc<Vec<Placement>>>,
    resources: HashMap<PathBuf, Arc<Vec<Transform>>>,
    foliage: HashMap<PathBuf, Arc<Vec<(Transform, bool)>>>,
    procedural: HashMap<(i32, i32, i32), Arc<ProceduralRegion>>,
}

#[derive(Component)]
pub struct WorldObject {
    pub guid: String,
    pub asset_name: String,
    /// Stable row in the source placement file, independent of streaming region.
    pub source_index: usize,
}
#[derive(Component)]
pub struct RoadSurface {
    pub concrete: bool,
}
#[derive(Component)]
pub struct WorldTerrain;
#[derive(Component)]
pub struct WorldFoliage;
#[derive(Component, Clone)]
pub struct WorldResource {
    pub name: String,
    /// Row in this resource's original .bin, before distance culling.
    pub instance_index: usize,
}
#[derive(Clone, Debug)]
pub struct Placement {
    pub source_index: usize,
    pub guid: String,
    pub name: String,
    pub material: String,
    pub transform: Transform,
}

/// Retail placement conversion shared by objects and resources. See WorldBuilder.cs:970.
pub fn placement_transform(p: Vec3, euler_degrees: Vec3, scale: Vec3) -> Transform {
    Transform {
        translation: Vec3::new(p.x, p.y, -p.z),
        rotation: Quat::from_rotation_y((180. - euler_degrees.y).to_radians())
            * Quat::from_rotation_x(euler_degrees.x.to_radians())
            * Quat::from_rotation_z(-euler_degrees.z.to_radians()),
        scale,
    }
}
pub fn parse_placements(
    source: &str,
    names: &HashMap<String, String>,
) -> Result<Vec<Placement>, ContentError> {
    let mut result = Vec::new();
    for (line_no, line) in source.lines().enumerate() {
        let fields: Vec<_> = line
            .split('#')
            .next()
            .unwrap_or("")
            .split_whitespace()
            .collect();
        if fields.is_empty() {
            continue;
        }
        if fields.len() < 10 {
            return Err(ContentError(format!(
                "placement line {}: expected GUID and nine transform values",
                line_no + 1
            )));
        }
        let guid = fields[0].to_ascii_lowercase();
        let Some(name) = names.get(&guid) else {
            continue;
        };
        let mut n = [0.; 9];
        for (i, out) in n.iter_mut().enumerate() {
            *out = fields[i + 1].parse::<f32>().map_err(|_| {
                ContentError(format!("placement line {}: invalid transform", line_no + 1))
            })?;
            if !out.is_finite() {
                return Err(ContentError(format!(
                    "placement line {}: non-finite transform",
                    line_no + 1
                )));
            }
        }
        result.push(Placement {
            source_index: line_no,
            guid,
            name: name.clone(),
            material: fields.get(10).copied().unwrap_or(name).into(),
            transform: placement_transform(
                Vec3::new(n[0], n[1], n[2]),
                Vec3::new(n[3], n[4], n[5]),
                Vec3::new(n[6], n[7], n[8]),
            ),
        });
    }
    Ok(result)
}

/// A terrain patch has increasing X/Z coordinates and absolute world heights.
#[derive(Clone, Debug)]
pub struct TerrainPatch {
    pub origin: Vec2,
    pub cells: usize,
    pub spacing: f32,
    pub heights: Vec<f32>,
    pub layers: Vec<u8>,
    pub normals: Vec<[f32; 3]>,
    pub holes: Vec<bool>,
    pub splat: Option<Arc<terrain::SplatField>>,
}
impl TerrainPatch {
    pub fn sample(&self, x: f32, z: f32) -> Option<f32> {
        let p = (Vec2::new(x, z) - self.origin) / self.spacing;
        if p.x < 0. || p.y < 0. || p.x > self.cells as f32 || p.y > self.cells as f32 {
            return None;
        }
        let ix = (p.x.floor() as usize).min(self.cells - 1);
        let iz = (p.y.floor() as usize).min(self.cells - 1);
        let s = self.cells + 1;
        let a = iz * s + ix;
        let tx = p.x - ix as f32;
        let tz = p.y - iz as f32;
        if [a, a + 1, a + s, a + s + 1]
            .iter()
            .any(|&i| self.holes.get(i) == Some(&true))
        {
            return None;
        }
        Some(
            (self.heights[a] * (1. - tx) + self.heights[a + 1] * tx) * (1. - tz)
                + (self.heights[a + s] * (1. - tx) + self.heights[a + s + 1] * tx) * tz,
        )
    }
    pub fn normal(&self, x: usize, z: usize) -> Vec3 {
        let s = self.cells + 1;
        if let Some(n) = self.normals.get(z * s + x) {
            return Vec3::from_array(*n);
        }
        let x0 = x.saturating_sub(1);
        let x1 = (x + 1).min(self.cells);
        let z0 = z.saturating_sub(1);
        let z1 = (z + 1).min(self.cells);
        let dx = (self.heights[z * s + x1] - self.heights[z * s + x0])
            / ((x1 - x0) as f32 * self.spacing);
        let dz = (self.heights[z1 * s + x] - self.heights[z0 * s + x])
            / ((z1 - z0) as f32 * self.spacing);
        Vec3::new(-dx, 1., -dz).normalize()
    }
    pub fn mesh_by_layer(&self, center: Vec3, radius: f32) -> [ObjData; 8] {
        let mut out: [ObjData; 8] = std::array::from_fn(|_| ObjData::default());
        let stride = self.cells + 1;
        for z in 0..self.cells {
            for x in 0..self.cells {
                let point = self.origin + Vec2::new(x as f32, z as f32) * self.spacing;
                if point.distance(Vec2::new(center.x, center.z)) > radius {
                    continue;
                }
                let a = z * stride + x;
                if [a, a + 1, a + stride, a + stride + 1]
                    .iter()
                    .any(|&i| self.holes.get(i) == Some(&true))
                {
                    continue;
                }
                let layer = usize::from(self.layers.get(a).copied().unwrap_or(2).min(7));
                for (vx, vz) in [
                    (x, z),
                    (x, z + 1),
                    (x + 1, z),
                    (x + 1, z),
                    (x, z + 1),
                    (x + 1, z + 1),
                ] {
                    let wx = self.origin.x + vx as f32 * self.spacing;
                    let wz = self.origin.y + vz as f32 * self.spacing;
                    out[layer]
                        .positions
                        .push([wx, self.heights[vz * stride + vx], wz]);
                    out[layer].normals.push(self.normal(vx, vz).to_array());
                    out[layer].uvs.push([wx / 16., wz / 16.]);
                    out[layer].colors.push([1.; 4]);
                }
            }
        }
        out
    }
}
/// Big-endian 257² u16 samples, with the retail X/Z transpose and Z reflection.
pub fn decode_retail_heightmap(
    bytes: &[u8],
    tile_x: i32,
    tile_z: i32,
) -> Result<TerrainPatch, ContentError> {
    const S: usize = 257;
    if bytes.len() != S * S * 2 {
        return Err(ContentError(format!(
            "heightmap: expected {} bytes, got {}",
            S * S * 2,
            bytes.len()
        )));
    }
    let mut heights = vec![0.; S * S];
    for z in 0..S {
        for x in 0..S {
            let i = ((S - 1 - z) * S + x) * 2;
            heights[z * S + x] =
                u16::from_be_bytes([bytes[i], bytes[i + 1]]) as f32 / 65535. * 2048. - 1024.;
        }
    }
    Ok(TerrainPatch {
        origin: Vec2::new(tile_x as f32 * 1024., -(tile_z as f32 + 1.) * 1024.),
        cells: 256,
        spacing: 4.,
        heights,
        layers: vec![2; S * S],
        holes: Vec::new(),
        normals: Vec::new(),
        splat: None,
    })
}
fn load_retail_terrain(root: &Path) -> Result<Vec<TerrainPatch>, ContentError> {
    let mut result = Vec::new();
    let dir = root.join("Landscape/Heightmaps");
    let mut files: Vec<_> = fs::read_dir(&dir)?
        .filter_map(Result::ok)
        .map(|x| x.path())
        .collect();
    files.sort();
    for path in files {
        let Some(stem) = path.file_name().and_then(|s| s.to_str()) else {
            continue;
        };
        if !stem.starts_with("Tile_") || !stem.ends_with("_Source.heightmap") {
            continue;
        }
        let fields: Vec<_> = stem.split('_').collect();
        let (Ok(x), Ok(z)) = (fields[1].parse(), fields[2].parse()) else {
            continue;
        };
        let mut patch = decode_retail_heightmap(&fs::read(&path)?, x, z)?;
        let splat = root.join(format!("Landscape/Splatmaps/Tile_{x}_{z}_Source.splatmap"));
        if splat.is_file() {
            let data = fs::read(splat)?;
            if data.len() != 256 * 256 * 8 {
                return Err(ContentError("splatmap length is not 256 × 256 × 8".into()));
            }
            patch.splat = Some(Arc::new(terrain::SplatField::retail(&data, patch.origin)?));
            for gz in 0..257 {
                for gx in 0..257 {
                    let i = (((256 - gz).min(255)) * 256 + gx.min(255)) * 8;
                    patch.layers[gz * 257 + gx] = layers::dominant(&data[i..i + 8]);
                }
            }
        }
        result.push(patch);
    }
    if result.is_empty() {
        return Err(ContentError(format!(
            "no terrain tiles in {}",
            dir.display()
        )));
    }
    let merged = Arc::new(terrain::SplatField::merge_retail(&result));
    for patch in &mut result {
        patch.splat = Some(merged.clone());
    }
    Ok(result)
}

#[derive(Clone)]
pub struct ObjectCollider {
    pub source_index: Option<usize>,
    pub resource_key: Option<(String, usize)>,
    pub min: Vec3,
    pub max: Vec3,
    pub transform: Mat4,
    pub inverse: Mat4,
    pub geometry: Arc<ObjData>,
}
#[derive(Resource, Default)]
pub struct WorldGeometry {
    /// Increments when collision geometry changes; authority must refresh its copy.
    pub revision: u64,
    pub loaded_center: Vec3,
    pub loaded_radius: f32,
    stream_anchor: Vec3,
    pub terrain: Vec<TerrainPatch>,
    pub objects: Vec<ObjectCollider>,
}
impl WorldGeometry {
    pub fn terrain_height(&self, x: f32, z: f32) -> Option<f32> {
        self.terrain.iter().find_map(|t| t.sample(x, z))
    }
    /// Exact mesh segment cast; broad phase rejects bounds before triangle work.
    pub fn raycast(&self, origin: Vec3, direction: Vec3, max_distance: f32) -> Option<(f32, Vec3)> {
        if direction.length_squared() < 1e-12 {
            return None;
        }
        let dir = direction.normalize();
        let mut closest = max_distance;
        let mut hit = None;
        for object in &self.objects {
            if !ray_box(origin, dir, object.min, object.max, closest) {
                continue;
            }
            let p = object.inverse.transform_point3(origin);
            let d = object.inverse.transform_vector3(dir);
            for tri in object.geometry.positions.chunks_exact(3) {
                let a = Vec3::from_array(tri[0]);
                let b = Vec3::from_array(tri[1]);
                let c = Vec3::from_array(tri[2]);
                let e1 = b - a;
                let e2 = c - a;
                let h = d.cross(e2);
                let det = e1.dot(h);
                if det.abs() < 1e-8 {
                    continue;
                }
                let inv = 1. / det;
                let s = p - a;
                let u = inv * s.dot(h);
                if !(0.0..=1.).contains(&u) {
                    continue;
                }
                let q = s.cross(e1);
                let v = inv * d.dot(q);
                if v < 0. || u + v > 1. {
                    continue;
                }
                let t = inv * e2.dot(q);
                if t >= 0. && t < closest {
                    closest = t;
                    hit = Some((t, origin + dir * t));
                }
            }
        }
        hit
    }
}
fn ray_box(origin: Vec3, dir: Vec3, min: Vec3, max: Vec3, limit: f32) -> bool {
    let mut near = 0_f32;
    let mut far = limit;
    for i in 0..3 {
        if dir[i].abs() < 1e-8 {
            if origin[i] < min[i] || origin[i] > max[i] {
                return false;
            }
            continue;
        }
        let a = (min[i] - origin[i]) / dir[i];
        let b = (max[i] - origin[i]) / dir[i];
        near = near.max(a.min(b));
        far = far.min(a.max(b));
        if near > far {
            return false;
        }
    }
    true
}
fn collider(data: Arc<ObjData>, transform: Transform) -> ObjectCollider {
    let matrix = transform.to_matrix();
    let mut min = Vec3::splat(f32::INFINITY);
    let mut max = Vec3::splat(f32::NEG_INFINITY);
    for x in [data.min.x, data.max.x] {
        for y in [data.min.y, data.max.y] {
            for z in [data.min.z, data.max.z] {
                let p = matrix.transform_point3(Vec3::new(x, y, z));
                min = min.min(p);
                max = max.max(p);
            }
        }
    }
    ObjectCollider {
        source_index: None,
        resource_key: None,
        min,
        max,
        transform: matrix,
        inverse: matrix.inverse(),
        geometry: data,
    }
}

fn build_world(
    mut commands: Commands,
    config: Res<WorldConfig>,
    options: Res<crate::LaunchOptions>,
    bridge: Res<crate::bridge::CoreBridge>,
    mut state: ResMut<WorldState>,
    mut geometry: ResMut<WorldGeometry>,
    mut cache: ResMut<ContentCache>,
    mut sources: ResMut<WorldSourceCache>,
    mut origin: ResMut<origin::WorldOrigin>,
    players: Query<&Transform, With<crate::player::PlayerBody>>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
    existing: Query<Entity, Or<(With<WorldObject>, With<WorldTerrain>, With<WorldFoliage>)>>,
) {
    for entity in &existing {
        commands.entity(entity).despawn();
    }
    let streaming = state.ready;
    let procedural_start = config
        .procedural_path
        .as_ref()
        .and_then(|_| std::env::var("UG_INF_AT").ok())
        .and_then(|s| origin::parse_absolute(&s))
        .filter(|v| v.len() >= 2);
    // Camera/config coordinates remain absolute. Geometry and physics are local.
    let absolute_center = config
        .fixed_camera
        .map(|c| c.0)
        .or(config.spawn_center.map(Vec3::as_dvec3))
        .or_else(|| {
            procedural_start
                .as_ref()
                .map(|v| DVec3::new(v[0], 0., v[1]))
        });
    if !streaming {
        origin.offset = if config.procedural_path.is_some() {
            let p = absolute_center.unwrap_or(DVec3::new(128., 0., 128.));
            (p.xz() / 256.).floor() * 256.
        } else {
            default()
        };
        origin.epoch = 0;
    }
    let explicit_center = absolute_center.map(|p| origin.local(p));
    let old_spawn = state.spawn;
    let old_yaw = state.spawn_yaw;
    let old_focus = state.focus;
    let old_description = state.description.clone();
    if !streaming {
        geometry.terrain.clear();
    }
    geometry.objects.clear();
    *state = WorldState::default();
    if streaming {
        state.description = old_description;
    }
    let suffix = match config.map_name.to_ascii_lowercase().as_str() {
        "washington" => "_Washington",
        "yukon" => "_Yukon",
        _ => "",
    };
    let terrain_dir = match suffix {
        "_Washington" => "terrain_washington",
        "_Yukon" => "terrain_yukon",
        _ => "terrain",
    };
    let mut proc_regions = Vec::new();
    if let Some(path) = &config.procedural_path {
        let center = if let Some((position, _, _)) = config.fixed_camera {
            origin.local(position)
        } else if streaming {
            players
                .iter()
                .next()
                .map(|p| p.translation)
                .unwrap_or(old_spawn)
        } else {
            explicit_center.unwrap_or(Vec3::new(128., 0., 128.))
        };
        match procedural::regions(
            &bridge,
            options.0.seed,
            origin.to_absolute(center),
            origin.offset,
            config.spawn_radius + 48.,
            path,
            &mut sources.procedural,
        ) {
            Ok(regions) => {
                geometry.terrain = regions.iter().filter_map(|r| r.patch().ok()).collect();
                proc_regions = regions;
                state.description = "Original procedural terrain generator".into();
            }
            Err(e) => state.missing_assets.push(e.to_string()),
        }
    } else if !streaming {
        if let Some(root) = &config.map_root {
            match load_retail_terrain(root) {
                Ok(patches) => {
                    geometry.terrain = patches;
                    state.description = format!("{} — original retail terrain", config.map_name);
                }
                Err(e) => state.missing_assets.push(e.to_string()),
            }
        }
    }
    state.terrain_available = !geometry.terrain.is_empty();
    state.sea_level = if state.terrain_available {
        Some(25.6)
    } else {
        None
    };
    if let Some(root) = &config.map_root {
        if let Ok(bytes) = fs::read(root.join("Environment/Lighting.dat")) {
            if bytes.len() >= 22 {
                let sea = f32::from_le_bytes(bytes[18..22].try_into().unwrap());
                if sea.is_finite() && (0.0..=1.0).contains(&sea) {
                    state.sea_level = if sea >= 0.99 { None } else { Some(sea * 256.) };
                }
            }
        }
    }
    if std::env::var("UG_NOWATER").as_deref() == Ok("1") {
        state.sea_level = None;
    }
    if let Some(sea) = state.sea_level {
        let focus = if streaming {
            players.iter().next().map(|p| p.translation).unwrap_or(old_spawn)
        } else {
            explicit_center.unwrap_or(Vec3::ZERO)
        };
        let (center, size) = water::ocean_bounds(
            &geometry.terrain,
            config.procedural_path.is_some(),
            focus,
        );
        // Current source WaveField.Flat never displaces vertices. A flat quad
        // produces the same visible surface; the opaque terrain depth hides land.
        let mesh = meshes.add(Plane3d::default().mesh().size(size.x, size.y));
        let material = materials.add(StandardMaterial::default());
        commands.spawn((
            Name::new("Source ocean"),
            WorldTerrain,
            water::WaterSurface,
            Mesh3d(mesh),
            MeshMaterial3d(material),
            Transform::from_xyz(center.x, sea, center.y),
        ));
    }

    let names = match load_guid_map(&config.content_root) {
        Ok(n) => n,
        Err(e) => {
            state.missing_assets.push(e.to_string());
            HashMap::new()
        }
    };
    let placements = if config.procedural_path.is_some() {
        Arc::new(Vec::new())
    } else {
        let path = config
            .content_root
            .join(format!("objects/placements{suffix}.txt"));
        if let Some(p) = sources.placements.get(&path) {
            p.clone()
        } else {
            match fs::read_to_string(&path)
                .map_err(ContentError::from)
                .and_then(|t| parse_placements(&t, &names))
            {
                Ok(p) => {
                    let p = Arc::new(p);
                    sources.placements.insert(path, p.clone());
                    p
                }
                Err(e) => {
                    state.missing_assets.push(e.to_string());
                    Arc::new(Vec::new())
                }
            }
        }
    };
    let center = if let Some((position, _, _)) = config.fixed_camera {
        origin.local(position)
    } else if streaming {
        players
            .iter()
            .next()
            .map(|p| p.translation)
            .unwrap_or(old_spawn)
    } else {
        explicit_center.unwrap_or_else(|| {
            if let Some(patch) = geometry
                .terrain
                .first()
                .filter(|_| config.procedural_path.is_some())
            {
                return Vec3::new(
                    patch.origin.x + patch.cells as f32 * patch.spacing * 0.5,
                    0.,
                    patch.origin.y + patch.cells as f32 * patch.spacing * 0.5,
                );
            }
            let mut cells: HashMap<(i32, i32), (usize, Vec3)> = HashMap::new();
            for p in placements.iter() {
                let v = p.transform.translation;
                let e = cells
                    .entry(((v.x / 128.).floor() as i32, (v.z / 128.).floor() as i32))
                    .or_default();
                e.0 += 1;
                e.1 += v;
            }
            cells
                .into_iter()
                .max_by_key(|(k, (n, _))| (*n, *k))
                .map(|(_, (n, sum))| sum / n as f32)
                .unwrap_or(Vec3::ZERO)
        })
    };
    state.focus = if streaming {
        old_focus
    } else if config.procedural_path.is_some() {
        Vec3::new(
            center.x,
            geometry
                .terrain_height(center.x, center.z)
                .unwrap_or(center.y),
            center.z,
        )
    } else {
        center
    };
    let player_spawn = if streaming {
        Some((old_spawn, old_yaw))
    } else if config.spawn_center.is_none() && config.procedural_path.is_none() {
        config
            .map_root
            .as_ref()
            .and_then(|root| fs::read(root.join("Spawns/Players.dat")).ok())
            .and_then(|bytes| decode_player_spawns(&bytes).ok())
            .and_then(|spawns| {
                spawns
                    .into_iter()
                    .filter(|(p, _)| geometry.terrain_height(p.x, p.z).is_some())
                    .min_by(|(a, _), (b, _)| {
                        a.xz()
                            .distance_squared(center.xz())
                            .total_cmp(&b.xz().distance_squared(center.xz()))
                    })
            })
    } else {
        None
    };
    let (spawn_point, yaw) = player_spawn.unwrap_or((
        center,
        procedural_start
            .as_ref()
            .and_then(|v| v.get(2).copied())
            .unwrap_or(0.) as f32,
    ));
    state.spawn = Vec3::new(
        spawn_point.x,
        geometry
            .terrain_height(spawn_point.x, spawn_point.z)
            .unwrap_or(spawn_point.y)
            + 0.5,
        spawn_point.z,
    );
    state.spawn_yaw = yaw;
    if streaming {
        state.spawn = old_spawn;
    }
    let render_radius = if streaming {
        config.spawn_radius.max(96.)
    } else {
        config
            .spawn_radius
            .max(center.xz().distance(state.spawn.xz()) + 48.)
    };
    geometry.loaded_center = center;
    geometry.loaded_radius = render_radius + 48.;
    geometry.stream_anchor = if streaming || config.fixed_camera.is_some() {
        center
    } else {
        state.spawn
    };
    geometry.revision = geometry.revision.wrapping_add(1);
    if !state.terrain_available {
        state.description = format!(
            "{} extracted prop viewer — retail terrain unavailable",
            config.map_name
        );
    }
    for patch in &geometry.terrain {
        let mut data = ObjData::default();
        for layer in patch.mesh_by_layer(center, render_radius + 48.) {
            data.append_transformed(&layer, Transform::IDENTITY);
        }
        if data.positions.is_empty() {
            continue;
        }
        let shape = trimesh_collider(&data);
        let fallback = cache.material(
            Some(&format!("{terrain_dir}/layer2.png")),
            false,
            &assets,
            &mut materials,
        );
        let mut entity = commands.spawn((
            Name::new("Terrain splat surface"),
            WorldTerrain,
            Mesh3d(meshes.add(data.mesh())),
            MeshMaterial3d(fallback),
            terrain::TerrainSurface {
                splat: terrain::SplatField::for_patch(patch),
                root: config.content_root.clone(),
                directories: std::array::from_fn(|layer| {
                    if config.procedural_path.is_some() && matches!(layer, 0 | 3 | 6 | 7) {
                        "terrain_yukon".into()
                    } else {
                        terrain_dir.into()
                    }
                }),
            },
        ));
        if let Some(shape) = shape {
            entity.insert((RigidBody::Fixed, shape));
        }
    }
    let within = |p: Vec3| Vec2::new(p.x - center.x, p.z - center.z).length() <= render_radius;
    for p in placements
        .iter()
        .filter(|p| within(p.transform.translation))
    {
        let path = config.content_root.join(format!("objects/{}.obj", p.name));
        let Ok((mesh, data)) = cache.load_mesh(&path, &mut meshes) else {
            state.missing_assets.push(path.display().to_string());
            continue;
        };
        let texture = format!("objects/{}_tex.png", p.material);
        let texture = if config.content_root.join(&texture).is_file() {
            Some(texture)
        } else {
            None
        };
        let prop_key = format!("prop:{}", p.material);
        let material = if let Some(handle) = cache.materials.get(&prop_key) {
            handle.clone()
        } else {
            let cutout = texture
                .as_ref()
                .is_some_and(|t| cache.has_cutout_alpha(&config.content_root.join(t)));
            let base = cache.material(texture.as_deref(), cutout, &assets, &mut materials);
            let mut mat = materials.get(&base).unwrap().clone();
            mat.double_sided = true;
            mat.cull_mode = None;
            if cutout {
                mat.alpha_mode = AlphaMode::Mask(0.5);
            }
            if texture.is_none() {
                mat.base_color = Color::srgb(0.60, 0.55, 0.47);
            }
            if p.material.starts_with("Glass") {
                mat.base_color = Color::srgba(0.62, 0.73, 0.78, 0.26);
                mat.alpha_mode = AlphaMode::Blend;
                mat.perceptual_roughness = 0.06;
            }
            if p.material.starts_with("Biodome") {
                mat.base_color = Color::srgb(0.90, 0.53, 0.20);
                mat.base_color_texture = None;
                mat.perceptual_roughness = 0.6;
                mat.metallic = 0.05;
            }
            let handle = materials.add(mat);
            cache.materials.insert(prop_key, handle.clone());
            handle
        };
        let solid = !is_walk_through(&p.name);
        let collision_data = if p.name.starts_with("Ladder_") {
            Arc::new(ladder_box_geometry(&data))
        } else {
            data.clone()
        };
        let shape = if !solid {
            None
        } else if p.name.starts_with("Ladder_") {
            let half = (data.max - data.min) * 0.5;
            Some(Collider::cuboid(half.x, half.y, half.z))
        } else {
            trimesh_collider(&data)
        };
        if solid {
            let mut body = collider(collision_data, p.transform);
            body.source_index = Some(p.source_index);
            geometry.objects.push(body);
        }
        let mut entity = commands.spawn((
            Name::new(p.name.clone()),
            Mesh3d(mesh),
            MeshMaterial3d(material),
            p.transform,
            WorldObject {
                guid: p.guid.clone(),
                asset_name: p.name.clone(),
                source_index: p.source_index,
            },
        ));
        if let Some(shape) = shape {
            entity.insert((RigidBody::Fixed, shape));
        }
        state.object_count += 1;
    }
    if config.procedural_path.is_none() {
        if let Some(root) = &config.map_root {
            let load_roads = || -> Result<_, ContentError> {
                let env = root.join("Environment");
                Ok((
                    roads::decode_materials(&fs::read(env.join("Roads.dat"))?)?,
                    roads::decode_paths(&fs::read(env.join("Paths.dat"))?)?,
                ))
            };
            match load_roads() {
                Ok((road_materials, paths)) => {
                    for (index, road) in paths.iter().enumerate() {
                        let Some(mat) = road_materials.get(road.material) else {
                            continue;
                        };
                        let (visible, solid) =
                            roads::build(road, mat, &geometry, center, render_radius);
                        if visible.positions.is_empty() {
                            continue;
                        }
                        let texture = format!("roads/road_{}.png", road.material);
                        let material =
                            cache.material(Some(&texture), false, &assets, &mut materials);
                        let solid = Arc::new(solid);
                        let shape = trimesh_collider(&solid);
                        geometry.objects.push(collider(solid, Transform::IDENTITY));
                        let mut entity = commands.spawn((
                            Name::new(format!("Road spline {index}")),
                            WorldObject {
                                guid: road.guid.iter().map(|b| format!("{b:02x}")).collect(),
                                asset_name: format!("Road_{}", road.material),
                                source_index: index,
                            },
                            Mesh3d(meshes.add(visible.mesh())),
                            MeshMaterial3d(material),
                        ));
                        if let Some(shape) = shape {
                            entity.insert((RigidBody::Fixed, shape));
                        }
                        // Kept for the weather surface adapter; asphalt receives
                        // wet-surface effects, dirt trails deliberately do not.
                        entity.insert(RoadSurface {
                            concrete: mat.concrete,
                        });
                        state.object_count += 1;
                    }
                }
                Err(error) => state.missing_assets.push(format!("road splines: {error}")),
            }
        }
    }
    if config.foliage && config.procedural_path.is_none() {
        let resource_dir = match suffix {
            "_Washington" => "resources_washington",
            "_Yukon" => "resources_yukon",
            _ => "resources",
        };
        if let Ok(manifest) =
            fs::read_to_string(config.content_root.join(resource_dir).join("resources.txt"))
        {
            for line in manifest.lines() {
                let f: Vec<_> = line.split_whitespace().collect();
                if f.len() < 2 || f.get(2).is_some_and(|h| *h != "NONE") {
                    continue;
                }
                let Ok(parts) = f[1].parse::<usize>() else {
                    continue;
                };
                let path = config
                    .content_root
                    .join(resource_dir)
                    .join(format!("{}.bin", f[0]));
                let transforms = if let Some(t) = sources.resources.get(&path) {
                    t.clone()
                } else {
                    let Ok(t) = fs::read(&path)
                        .map_err(ContentError::from)
                        .and_then(|b| decode_resource_instances(&b))
                    else {
                        continue;
                    };
                    let t = Arc::new(t);
                    sources.resources.insert(path, t.clone());
                    t
                };
                for (instance_index, mut transform) in transforms
                    .iter()
                    .copied()
                    .enumerate()
                    .filter(|(_, t)| within(t.translation))
                {
                    let resource = WorldResource {
                        name: f[0].to_string(),
                        instance_index,
                    };
                    if is_tree(f[0]) {
                        transform.translation.y -= 0.5 * transform.scale.y.abs();
                    }
                    if let Some((shape, data, body_transform)) = resource_shape(f[0], transform) {
                        let mut exported = collider(Arc::new(data), body_transform);
                        exported.resource_key = Some((f[0].to_string(), instance_index));
                        geometry.objects.push(exported);
                        commands.spawn((
                            Name::new(format!("{} resource body", f[0])),
                            WorldFoliage,
                            resource.clone(),
                            RigidBody::Fixed,
                            shape,
                            body_transform,
                        ));
                    }
                    for part in 0..parts {
                        let name = format!("{}_{}", f[0], part);
                        let path = config
                            .content_root
                            .join(resource_dir)
                            .join(format!("{name}.obj"));
                        if !path.is_file() {
                            continue;
                        }
                        let Ok((mesh, _)) = cache.load_mesh(&path, &mut meshes) else {
                            continue;
                        };
                        let texture = format!("{resource_dir}/{name}_tex.png");
                        let tex = config
                            .content_root
                            .join(&texture)
                            .is_file()
                            .then_some(texture);
                        let material =
                            cache.material(tex.as_deref(), true, &assets, &mut materials);
                        commands.spawn((
                            Name::new(name),
                            Mesh3d(mesh),
                            MeshMaterial3d(material),
                            transform,
                            WorldFoliage,
                            resource.clone(),
                            water::Reflective,
                        ));
                        state.foliage_count += 1;
                    }
                }
            }
        }
    }
    if config.foliage && config.procedural_path.is_none() {
        let foliage_dir = match suffix {
            "_Washington" => "foliage_washington",
            "_Yukon" => "foliage_yukon",
            _ => "foliage",
        };
        if let Ok(entries) = fs::read_dir(config.content_root.join(foliage_dir)) {
            let mut files: Vec<_> = entries
                .filter_map(Result::ok)
                .map(|e| e.path())
                .filter(|p| p.extension().is_some_and(|e| e == "bin"))
                .collect();
            files.sort();
            for bin in files {
                let Some(name) = bin.file_stem().and_then(|s| s.to_str()) else {
                    continue;
                };
                let Ok((_, source)) = cache.load_mesh(&bin.with_extension("obj"), &mut meshes)
                else {
                    continue;
                };
                let instances = if let Some(t) = sources.foliage.get(&bin) {
                    t.clone()
                } else {
                    let Ok(t) = fs::read(&bin)
                        .map_err(ContentError::from)
                        .and_then(|b| decode_foliage_instances(&b))
                    else {
                        continue;
                    };
                    let t = Arc::new(t);
                    sources.foliage.insert(bin.clone(), t.clone());
                    t
                };
                let mut merged = ObjData::default();
                for (mut transform, _) in instances.iter().copied() {
                    // Match the original Ultra foliage range (five 32m tiles).
                    if transform
                        .translation
                        .xz()
                        .distance(if streaming {
                            center.xz()
                        } else {
                            state.spawn.xz()
                        })
                        .min(transform.translation.xz().distance(center.xz()))
                        > 160.
                    {
                        continue;
                    }
                    if name.starts_with("pebble") {
                        transform.scale *= 0.75;
                    }
                    merged.append_transformed(&source, transform);
                    state.foliage_count += 1;
                }
                if merged.positions.is_empty() {
                    continue;
                }
                let texture = format!("{foliage_dir}/{name}_tex.png");
                let tex = config
                    .content_root
                    .join(&texture)
                    .is_file()
                    .then_some(texture);
                let material = cache.material(tex.as_deref(), true, &assets, &mut materials);
                commands.spawn((
                    Name::new(format!("Foliage {name}")),
                    WorldFoliage,
                    foliage::FoliageSurface(160.),
                    Mesh3d(meshes.add(merged.mesh())),
                    MeshMaterial3d(material),
                ));
            }
        }
    }
    for region in &proc_regions {
        let origin = Vec2::new(region.origin_x as f32, region.origin_z as f32);
        for tree in &region.trees {
            let name = region
                .structures
                .get("treeModels")
                .and_then(|k| k.get(tree.kind as usize))
                .and_then(|k| k.get(tree.variant as usize))
                .and_then(serde_json::Value::as_str)
                .map(str::to_owned)
                .or_else(|| procedural_tree_name(tree.kind, tree.variant));
            let Some(name) = name else {
                continue;
            };
            let transform = procedural_tree_transform(tree, origin);
            if !within(transform.translation) {
                continue;
            }
            let height = if tree.kind == 3 {
                14.
            } else if tree.kind == 4 {
                4.
            } else {
                8.
            };
            let radius = if tree.kind == 4 {
                if name.contains("Barrel") {
                    0.7
                } else if name.contains("Cluster") {
                    0.8
                } else if name.contains("Paddles") {
                    0.6
                } else if name.contains("Branched") {
                    0.5
                } else {
                    0.35
                }
            } else {
                tree_radius(&name)
            };
            commands.spawn((
                Name::new(format!("{name} trunk")),
                WorldFoliage,
                RigidBody::Fixed,
                Collider::cylinder(height * 0.5 * tree.scale, radius * tree.scale),
                Transform::from_translation(
                    transform.translation + Vec3::Y * (height * 0.5 - 1.5) * tree.scale,
                ),
            ));
            for part in 0..2 {
                let path = config
                    .content_root
                    .join(format!("resources/{name}_{part}.obj"));
                let Ok((mesh, _)) = cache.load_mesh(&path, &mut meshes) else {
                    continue;
                };
                let texture = format!("resources/{name}_{part}_tex.png");
                let tex = config
                    .content_root
                    .join(&texture)
                    .is_file()
                    .then_some(texture);
                let material = cache.material(tex.as_deref(), true, &assets, &mut materials);
                commands.spawn((
                    Name::new(name.clone()),
                    Mesh3d(mesh),
                    MeshMaterial3d(material),
                    transform,
                    WorldFoliage,
                    water::Reflective,
                ));
                state.foliage_count += 1;
            }
        }
    }
    for region in &proc_regions {
        for (texture, road) in procedural_roads::build(region) {
            if road.visible.positions.is_empty() {
                continue;
            }
            let material = match procedural_roads::material(
                &texture,
                &config.content_root,
                &mut cache,
                &assets,
                &mut materials,
                &mut images,
            ) {
                Ok(m) => m,
                Err(e) => {
                    state.missing_assets.push(e.to_string());
                    continue;
                }
            };
            let shape = if road.solid.positions.is_empty() {
                None
            } else {
                trimesh_collider(&road.solid)
            };
            if !road.solid.positions.is_empty() {
                geometry
                    .objects
                    .push(collider(Arc::new(road.solid), Transform::IDENTITY));
            }
            let mut entity = commands.spawn((
                Name::new(format!("Generated {texture}")),
                WorldTerrain,
                Mesh3d(meshes.add(road.visible.mesh())),
                MeshMaterial3d(material),
            ));
            if let Some(shape) = shape {
                entity.insert((RigidBody::Fixed, shape));
            }
        }
    }
    if proc_regions
        .iter()
        .any(|r| procedural::rows(r, "tunnels").next().is_some())
    {
        let path = config.content_root.join("objects/Tunnel_Line_0.obj");
        match cache.load_mesh(&path, &mut meshes) {
            Ok((_, profile)) => {
                for (kind, data) in tunnels::build(&proc_regions, &profile) {
                    if data.positions.is_empty() {
                        continue;
                    }
                    let material = if let Some(layer) = kind
                        .strip_prefix("terrain:")
                        .and_then(|s| s.parse::<usize>().ok())
                    {
                        let dir = if matches!(layer, 0 | 3 | 6 | 7) {
                            "terrain_yukon"
                        } else {
                            "terrain"
                        };
                        cache.material(
                            Some(&format!("{dir}/layer{layer}.png")),
                            false,
                            &assets,
                            &mut materials,
                        )
                    } else {
                        cache
                            .materials
                            .entry(format!("generated:{kind}"))
                            .or_insert_with(|| {
                                materials.add(StandardMaterial {
                                    base_color: if kind == "floor" {
                                        Color::srgb(0.40, 0.39, 0.37)
                                    } else {
                                        Color::srgb(0.60, 0.55, 0.47)
                                    },
                                    perceptual_roughness: 1.,
                                    double_sided: true,
                                    cull_mode: None,
                                    ..default()
                                })
                            })
                            .clone()
                    };
                    let shape = trimesh_collider(&data);
                    let mesh = meshes.add(data.mesh());
                    geometry
                        .objects
                        .push(collider(Arc::new(data), Transform::IDENTITY));
                    let mut e = commands.spawn((
                        Name::new(format!("Generated tunnel {kind}")),
                        WorldTerrain,
                        Mesh3d(mesh),
                        MeshMaterial3d(material),
                    ));
                    if let Some(shape) = shape {
                        e.insert((RigidBody::Fixed, shape));
                    }
                }
            }
            Err(e) => state.missing_assets.push(e.to_string()),
        }
    }
    let wire_mesh = if config.procedural_path.is_some() {
        wires::generated(&proc_regions, center, render_radius)
    } else {
        fs::read_to_string(
            config
                .content_root
                .join(format!("powerlines/editor_{}_wires.txt", config.map_name)),
        )
        .map(|source| wires::saved(&source, &placements, center, render_radius))
        .unwrap_or_default()
    };
    if !wire_mesh.positions.is_empty() {
        let material = cache
            .materials
            .entry("powerline_wire".into())
            .or_insert_with(|| {
                materials.add(StandardMaterial {
                    base_color: Color::srgb(0.055, 0.055, 0.062),
                    perceptual_roughness: 0.55,
                    reflectance: 0.35,
                    double_sided: true,
                    cull_mode: None,
                    ..default()
                })
            })
            .clone();
        commands.spawn((
            Name::new("Power conductors"),
            WorldFoliage,
            Mesh3d(meshes.add(wire_mesh.mesh())),
            MeshMaterial3d(material),
            bevy::light::NotShadowCaster,
        ));
    }
    let mut ground_cover: HashMap<String, (ObjData, Color)> = HashMap::new();
    for region in &proc_regions {
        for (index, instance) in procedural::instances(region).into_iter().enumerate() {
            let distance = instance.transform.translation.xz().distance(center.xz());
            let range = if instance.mesh.contains("pebble") {
                100.
            } else if instance.mesh.contains("Bush_") {
                320.
            } else if instance.foliage {
                160.
            } else {
                render_radius
            };
            if distance > range {
                continue;
            }
            let path = config.content_root.join(format!("{}.obj", instance.mesh));
            let Ok((mesh, data)) = cache.load_mesh(&path, &mut meshes) else {
                state.missing_assets.push(path.display().to_string());
                continue;
            };
            let mesh = if instance.mesh == "objects/Bridge_Line_1_Deck_Unit" {
                cache
                    .meshes
                    .entry(path.with_extension("deck_render"))
                    .or_insert_with(|| meshes.add(procedural_roads::deck_render_mesh(&data).mesh()))
                    .clone()
            } else {
                mesh
            };
            if instance.foliage {
                let group = ground_cover
                    .entry(instance.mesh)
                    .or_insert_with(|| (ObjData::default(), instance.tint));
                group.0.append_transformed(&data, instance.transform);
                state.foliage_count += 1;
                continue;
            }
            let texture = format!("{}_tex.png", instance.mesh);
            let texture = config
                .content_root
                .join(&texture)
                .is_file()
                .then_some(texture);
            let alpha = texture
                .as_ref()
                .is_some_and(|t| cache.has_cutout_alpha(&config.content_root.join(t)));
            let material = cache.material(texture.as_deref(), alpha, &assets, &mut materials);
            let shape = instance.solid.then(|| trimesh_collider(&data)).flatten();
            if instance.solid {
                geometry.objects.push(collider(data, instance.transform));
            }
            let mut entity = commands.spawn((
                Name::new(instance.mesh.clone()),
                WorldObject {
                    guid: String::new(),
                    asset_name: instance.mesh.trim_start_matches("objects/").into(),
                    source_index: index,
                },
                Mesh3d(mesh),
                MeshMaterial3d(material),
                instance.transform,
            ));
            if let Some(shape) = shape {
                entity.insert((RigidBody::Fixed, shape));
            }
            state.object_count += 1;
        }
    }
    for (name, (merged, tint)) in ground_cover {
        let texture = format!("{name}_tex.png");
        let texture = config
            .content_root
            .join(&texture)
            .is_file()
            .then_some(texture);
        let base = cache.material(
            texture.as_deref(),
            texture.is_some(),
            &assets,
            &mut materials,
        );
        let key = format!("proc_foliage:{name}");
        let material = if let Some(m) = cache.materials.get(&key) {
            m.clone()
        } else {
            let mut material = materials.get(&base).unwrap().clone();
            material.base_color = tint;
            material.double_sided = true;
            material.cull_mode = None;
            let m = materials.add(material);
            cache.materials.insert(key, m.clone());
            m
        };
        let needs_up_normal = name.starts_with("foliage/");
        let mut entity = commands.spawn((
            Name::new(name.clone()),
            WorldFoliage,
            Mesh3d(meshes.add(merged.mesh())),
            MeshMaterial3d(material),
        ));
        if needs_up_normal {
            entity.insert(foliage::FoliageSurface(if name.contains("pebble") {
                100.
            } else {
                160.
            }));
        }
    }
    state.missing_assets.sort();
    state.missing_assets.dedup();
    state.ready = true;
    info!(
        "{}: {} props, {} resource meshes, {} terrain patches, {} missing assets",
        state.description,
        state.object_count,
        state.foliage_count,
        geometry.terrain.len(),
        state.missing_assets.len()
    );
    for error in state.missing_assets.iter().take(8) {
        warn!("{error}");
    }
}

/// The original world's explicit pass-through list, rather than an alpha/size guess.
fn is_tree(name: &str) -> bool {
    ["Birch", "Pine", "Maple", "Redwood"]
        .iter()
        .any(|s| name.starts_with(s))
}
fn tree_radius(name: &str) -> f32 {
    if name.starts_with("Maple") {
        0.83
    } else if name.starts_with("Pine") {
        0.80
    } else {
        match name {
            "Redwood_0" => 2.46,
            "Redwood_1" => 1.70,
            "Redwood_2" => 2.92,
            _ => 0.5,
        }
    }
}
pub fn is_walk_through(name: &str) -> bool {
    [
        "Bush_",
        "Crop_",
        "Vines_",
        "Hedge_",
        "Grass_",
        "Cane_",
        "Mushroom_",
    ]
    .iter()
    .any(|p| name.starts_with(p))
}
/// WorldBuilder's solid ladder box: raw rungs contain centre-line gaps and cannot
/// serve the source's continuous 0.75m attach/hold ray.
fn ladder_box_geometry(mesh: &ObjData) -> ObjData {
    let h = (mesh.max - mesh.min) * 0.5;
    let mut out = ObjData {
        min: -h,
        max: h,
        ..default()
    };
    for (n, u, v) in [
        (Vec3::X, Vec3::Y, Vec3::Z),
        (Vec3::NEG_X, Vec3::Z, Vec3::Y),
        (Vec3::Y, Vec3::Z, Vec3::X),
        (Vec3::NEG_Y, Vec3::X, Vec3::Z),
        (Vec3::Z, Vec3::X, Vec3::Y),
        (Vec3::NEG_Z, Vec3::Y, Vec3::X),
    ] {
        for (a, b) in [
            (-1., -1.),
            (1., -1.),
            (1., 1.),
            (-1., -1.),
            (1., 1.),
            (-1., 1.),
        ] {
            out.positions.push(((n + u * a + v * b) * h).to_array());
            out.normals.push(n.to_array());
            out.uvs.push([0.; 2]);
            out.colors.push([1.; 4]);
        }
    }
    out
}
/// Original ResourceField collider dimensions on an orthonormal body. The
/// authority consumes the same bounds as native physics, with cylinders exported
/// as closed 64-sided meshes (at most 0.12% radial deviation from the analytic body).
fn resource_shape(name: &str, transform: Transform) -> Option<(Collider, ObjData, Transform)> {
    let radial = transform.scale.x.abs().max(transform.scale.z.abs());
    let vertical = transform.scale.y.abs();
    let (shape, data, local_center) = if is_tree(name) {
        let (radius, half_height) = (tree_radius(name) * radial, 4. * vertical);
        let mut data = ObjData::default();
        for i in 0..64 {
            let a = std::f32::consts::TAU * i as f32 / 64.;
            let b = std::f32::consts::TAU * (i + 1) as f32 / 64.;
            let (na, nb) = (
                Vec3::new(a.cos(), 0., a.sin()),
                Vec3::new(b.cos(), 0., b.sin()),
            );
            let (pa, pb) = (na * radius, nb * radius);
            let (top, bottom) = (Vec3::Y * half_height, Vec3::NEG_Y * half_height);
            roads::triangle(
                &mut data,
                [pa + bottom, pb + bottom, pa + top],
                [[0.; 2]; 3],
                [na, nb, na],
            );
            roads::triangle(
                &mut data,
                [pb + bottom, pb + top, pa + top],
                [[0.; 2]; 3],
                [nb, nb, na],
            );
            roads::triangle(
                &mut data,
                [top, pa + top, pb + top],
                [[0.; 2]; 3],
                [Vec3::Y; 3],
            );
            roads::triangle(
                &mut data,
                [bottom, pa + bottom, pb + bottom],
                [[0.; 2]; 3],
                [Vec3::NEG_Y; 3],
            );
        }
        (
            Collider::cylinder(half_height, radius),
            data,
            Vec3::Y * (2.5 * vertical),
        )
    } else if name.starts_with("Metal") {
        let half = Vec3::new(1.3 * radial, 1.2 * vertical, 1.3 * radial);
        let data = ladder_box_geometry(&ObjData {
            min: -half,
            max: half,
            ..default()
        });
        (
            Collider::cuboid(half.x, half.y, half.z),
            data,
            Vec3::Y * (1.1 * vertical),
        )
    } else {
        return None;
    };
    Some((
        shape,
        data,
        Transform {
            translation: transform.translation + transform.rotation * local_center,
            rotation: transform.rotation,
            ..default()
        },
    ))
}
fn trimesh_collider(data: &ObjData) -> Option<Collider> {
    let vertices = data
        .positions
        .iter()
        .copied()
        .map(Vec3::from_array)
        .collect();
    let indices = (0..data.positions.len() as u32)
        .step_by(3)
        .map(|i| [i, i + 1, i + 2])
        .collect();
    Collider::trimesh(vertices, indices).ok()
}

/// FoliageField's v1 int32-count + twelve-float transforms, and v2 negative
/// marker/version/count + one manual-placement byte per transform.
pub fn decode_foliage_instances(bytes: &[u8]) -> Result<Vec<(Transform, bool)>, ContentError> {
    if bytes.len() < 4 {
        return Err(ContentError("truncated foliage header".into()));
    }
    let mut count = i32::from_le_bytes(bytes[..4].try_into().unwrap());
    let mut start = 4;
    let mut version = 1;
    if count < 0 {
        if bytes.len() < 12 {
            return Err(ContentError("truncated foliage version header".into()));
        }
        version = i32::from_le_bytes(bytes[4..8].try_into().unwrap());
        count = i32::from_le_bytes(bytes[8..12].try_into().unwrap());
        start = 12;
    }
    let stride = if version >= 2 { 49 } else { 48 };
    if !(1..=2).contains(&version) || count < 0 || bytes.len() != start + count as usize * stride {
        return Err(ContentError("invalid foliage count/version/length".into()));
    }
    let mut result = Vec::with_capacity(count as usize);
    for row in bytes[start..].chunks_exact(stride) {
        let mut f = [0.; 12];
        for (i, b) in row[..48].chunks_exact(4).enumerate() {
            f[i] = f32::from_le_bytes(b.try_into().unwrap());
        }
        if f.iter().any(|n| !n.is_finite()) {
            return Err(ContentError("non-finite foliage transform".into()));
        }
        let matrix = Mat4::from_cols(
            Vec4::new(f[0], f[1], -f[2], 0.),
            Vec4::new(f[3], f[4], -f[5], 0.),
            Vec4::new(-f[6], -f[7], f[8], 0.),
            Vec4::new(f[9], f[10], -f[11], 1.),
        );
        result.push((Transform::from_matrix(matrix), version >= 2 && row[48] != 0));
    }
    Ok(result)
}

/// Original LevelSpawns.PlayerSpawns format: version/count u8, XYZ f32 LE,
/// angle u8 in two-degree units, and (v4+) alternate-spawn bool.
pub fn decode_player_spawns(bytes: &[u8]) -> Result<Vec<(Vec3, f32)>, ContentError> {
    if bytes.len() < 2 {
        return Err(ContentError("truncated player spawn header".into()));
    }
    let version = bytes[0];
    let count = usize::from(bytes[1]);
    let stride = if version > 3 { 14 } else { 13 };
    if bytes.len() < 2 + count * stride {
        return Err(ContentError("truncated player spawns".into()));
    }
    let mut spawns = Vec::new();
    for row in bytes[2..2 + count * stride].chunks_exact(stride) {
        let x = f32::from_le_bytes(row[0..4].try_into().unwrap());
        let y = f32::from_le_bytes(row[4..8].try_into().unwrap());
        let z = f32::from_le_bytes(row[8..12].try_into().unwrap());
        if !x.is_finite() || !y.is_finite() || !z.is_finite() {
            return Err(ContentError("non-finite player spawn".into()));
        }
        if (version > 3 && row[13] != 0) || x * x + z * z < 4. {
            continue;
        }
        spawns.push((Vec3::new(x, y, -z), -(row[12] as f32) * 2.));
    }
    Ok(spawns)
}

pub fn decode_resource_instances(bytes: &[u8]) -> Result<Vec<Transform>, ContentError> {
    if bytes.len() < 4 {
        return Err(ContentError("truncated resource header".into()));
    }
    let count = i32::from_le_bytes(bytes[..4].try_into().unwrap());
    if count < 0 || bytes.len() != 4 + (count as usize) * 36 {
        return Err(ContentError(
            "invalid resource instance count/length".into(),
        ));
    }
    let mut result = Vec::with_capacity(count as usize);
    for row in bytes[4..].chunks_exact(36) {
        let mut n = [0.; 9];
        for (i, v) in row.chunks_exact(4).enumerate() {
            n[i] = f32::from_le_bytes(v.try_into().unwrap());
        }
        if n.iter().any(|n| !n.is_finite()) {
            return Err(ContentError("non-finite resource transform".into()));
        }
        result.push(placement_transform(
            Vec3::new(n[0], n[1], n[2]),
            Vec3::new(n[3], n[4], n[5]),
            Vec3::new(n[6], n[7], n[8]),
        ));
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn placement_authority_identity_survives_unknown_guids_and_comments() {
        let names = HashMap::from([("known".to_string(), "House_0".to_string())]);
        let p = parse_placements(
            "unknown 0 0 0 0 0 0 1 1 1\n# comment\nknown 1 2 3 4 5 6 1 1 1",
            &names,
        )
        .unwrap();
        assert_eq!(p.len(), 1);
        assert_eq!(p[0].source_index, 2);
    }
    #[test]
    fn real_ladder_collision_covers_rung_gaps() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../game/content/objects/Ladder_Metal_0.obj");
        let mesh = Arc::new(ObjData::load(&root).unwrap());
        let origin = Vec3::new(0., -1., 0.37);
        let mut world = WorldGeometry::default();
        world
            .objects
            .push(collider(mesh.clone(), Transform::IDENTITY));
        assert!(
            world.raycast(origin, Vec3::Y, 2.).is_none(),
            "fixture ray must pass through a real rung gap"
        );
        world.objects[0] = collider(Arc::new(ladder_box_geometry(&mesh)), Transform::IDENTITY);
        let hit = world
            .raycast(origin, Vec3::Y, 2.)
            .expect("solid source ladder box must hold between rungs");
        assert!((hit.0 - 0.925).abs() < 0.0001);
    }
    #[test]
    fn streaming_recenters_before_player_reaches_unloaded_collision() {
        assert!(!stream_due(Vec3::new(60., 100., 0.), Vec3::ZERO, 180.));
        assert!(stream_due(Vec3::new(73., 0., 0.), Vec3::ZERO, 180.));
        // Teleports trigger one rebuild at their destination, not at intermediate cells.
        assert!(stream_due(Vec3::new(-1000., 0., 500.), Vec3::ZERO, 180.));
        assert!(!stream_due(
            Vec3::new(-1000., 0., 500.),
            Vec3::new(-1000., 0., 500.),
            180.
        ));
    }
    #[test]
    fn original_fixed_camera_knob_preserves_degrees_and_rejects_nonfinite_values() {
        assert_eq!(
            parse_capture_camera("-12916,70,-19678,88,-10"),
            Some((DVec3::new(-12916., 70., -19678.), 88., -10.))
        );
        assert_eq!(
            parse_capture_camera("1,2,3"),
            Some((DVec3::new(1., 2., 3.), 0., -20.))
        );
        assert!(parse_capture_camera("1,2").is_none());
        assert!(parse_capture_camera("NaN,2,3").is_none());
    }
    #[test]
    fn resource_collision_export_preserves_source_scaled_trunk_and_ore_bounds() {
        let transform = Transform {
            translation: Vec3::new(10., 20., 30.),
            scale: Vec3::new(2., 3., 1.),
            ..default()
        };
        let (_, trunk, body) = resource_shape("Pine_0", transform).unwrap();
        assert_eq!(trunk.min, Vec3::new(-1.6, -12., -1.6));
        assert_eq!(trunk.max, Vec3::new(1.6, 12., 1.6));
        assert_eq!(body.translation, Vec3::new(10., 27.5, 30.));
        let (_, ore, body) = resource_shape("Metal_0", transform).unwrap();
        assert!((ore.max - Vec3::new(2.6, 3.6, 2.6)).length() < 1e-6);
        assert!((body.translation - Vec3::new(10., 23.3, 30.)).length() < 1e-5);
        assert!(resource_shape("Bush_0", transform).is_none());
    }
    #[test]
    fn procedural_trees_keep_source_runtime_axes_and_sink() {
        let tree = ProceduralTree {
            x: 2.,
            y: 10.,
            z: 4.,
            yaw: 90.,
            scale: 2.,
            kind: 0,
            variant: 1,
        };
        let t = procedural_tree_transform(&tree, Vec2::new(100., 200.));
        assert_eq!(t.translation, Vec3::new(102., 9., 204.));
        assert!((t.rotation * Vec3::Y - Vec3::Y).length() < 1e-6);
        assert!((t.rotation * Vec3::X - Vec3::NEG_Z).length() < 1e-6);
        assert_eq!(
            procedural_tree_name(4, 7).as_deref(),
            Some("Cactus_Barrel_2")
        );
        assert!(procedural_tree_name(4, 14).is_none());
    }
    #[test]
    fn original_placement_converts_position_and_rotation() {
        let t = placement_transform(Vec3::new(2., 3., 4.), Vec3::new(270., 0., 0.), Vec3::ONE);
        assert_eq!(t.translation, Vec3::new(2., 3., -4.));
        assert!((t.rotation * Vec3::Z - Vec3::Y).length() < 1e-5);
    }
    #[test]
    fn heightmap_is_big_endian_and_flips_z() {
        let mut b = vec![0; 257 * 257 * 2];
        b[0] = 255;
        b[1] = 255;
        let p = decode_retail_heightmap(&b, 2, -1).unwrap();
        assert_eq!(p.origin, Vec2::new(2048., 0.));
        assert_eq!(p.sample(2048., 1024.), Some(1024.));
        assert_eq!(p.sample(2048., 0.), Some(-1024.));
        assert_eq!(p.sample(2047., 0.), None);
        assert!(decode_retail_heightmap(&b[..100], 0, 0).is_err());
    }
    #[test]
    fn sampling_respects_holes_and_bounds() {
        let mut p = TerrainPatch {
            origin: Vec2::ZERO,
            cells: 1,
            spacing: 4.,
            heights: vec![0., 4., 8., 12.],
            layers: vec![],
            holes: vec![],
            normals: vec![],
            splat: None,
        };
        assert_eq!(p.sample(2., 2.), Some(6.));
        assert_eq!(p.sample(5., 2.), None);
        p.holes = vec![true, false, false, false];
        assert_eq!(p.sample(2., 2.), None);
    }
    #[test]
    fn player_spawn_parser_excludes_alternate_and_origin_rows() {
        let mut bytes = vec![4, 3];
        for (x, z, alt) in [(10_f32, 20_f32, 0_u8), (50., 30., 1), (0., 0., 0)] {
            bytes.extend_from_slice(&x.to_le_bytes());
            bytes.extend_from_slice(&5_f32.to_le_bytes());
            bytes.extend_from_slice(&z.to_le_bytes());
            bytes.push(90);
            bytes.push(alt);
        }
        let spawns = decode_player_spawns(&bytes).unwrap();
        assert_eq!(spawns.len(), 1);
        assert_eq!(spawns[0], (Vec3::new(10., 5., -20.), -180.));
        assert!(decode_player_spawns(&bytes[..20]).is_err());
    }
    #[test]
    fn foliage_v1_v2_preserve_transform_and_manual_flag() {
        for version in [1, 2] {
            let mut bytes = Vec::new();
            if version == 2 {
                bytes.extend_from_slice(&(-1_i32).to_le_bytes());
                bytes.extend_from_slice(&2_i32.to_le_bytes());
            }
            bytes.extend_from_slice(&1_i32.to_le_bytes());
            for f in [1_f32, 0., 0., 0., 1., 0., 0., 0., 1., 3., 4., 5.] {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            if version == 2 {
                bytes.push(1);
            }
            let rows = decode_foliage_instances(&bytes).unwrap();
            assert_eq!(rows.len(), 1);
            assert_eq!(rows[0].0.translation, Vec3::new(3., 4., -5.));
            assert_eq!(rows[0].1, version == 2);
            assert!(decode_foliage_instances(&bytes[..bytes.len() - 1]).is_err());
        }
    }
    #[test]
    fn collision_respects_authored_triangle_positions() {
        let data =
            Arc::new(crate::content::parse_obj("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3").unwrap());
        let mut world = WorldGeometry::default();
        world
            .objects
            .push(collider(data, Transform::from_xyz(3., 2., 5.)));
        let hit = world
            .raycast(Vec3::new(3.2, 2.2, 10.), Vec3::NEG_Z, 20.)
            .unwrap();
        assert!((hit.0 - 5.).abs() < 1e-5);
        assert!(
            world
                .raycast(Vec3::new(0., 0., 10.), Vec3::NEG_Z, 20.)
                .is_none()
        );
    }
    #[test]
    fn rejects_truncated_resources() {
        assert!(decode_resource_instances(&[1, 0, 0, 0]).is_err());
    }
    #[test]
    fn real_maps_retain_guid_identity() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let names = load_guid_map(&root).unwrap();
        for name in [
            "placements.txt",
            "placements_Washington.txt",
            "placements_Yukon.txt",
        ] {
            let rows = parse_placements(
                &fs::read_to_string(root.join("objects").join(name)).unwrap(),
                &names,
            )
            .unwrap();
            assert!(rows.len() > 100);
            assert_eq!(rows[0].guid.len(), 32);
        }
    }
}
