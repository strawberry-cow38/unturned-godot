//! Original map/content assembly. Missing retail terrain is reported explicitly.
use crate::content::{ContentCache, ContentError, ObjData, load_guid_map};
use bevy::prelude::*;
use bevy_rapier3d::prelude::{Collider, RigidBody};
use serde::Deserialize;
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
        app.init_resource::<WorldConfig>()
            .init_resource::<WorldState>()
            .init_resource::<WorldGeometry>()
            .add_systems(Startup, build_world.in_set(WorldReady))
            .add_systems(
                Update,
                build_world.run_if(world_needs_reload).in_set(WorldReady),
            );
    }
}
/// Request a rebuild by changing WorldConfig, then setting WorldState.ready = false.
/// Existing world entities are retired and caches remain shared across reloads.
fn world_needs_reload(state: Res<WorldState>) -> bool {
    !state.ready
}

#[derive(Component)]
pub struct WorldObject {
    pub guid: String,
    pub asset_name: String,
}
#[derive(Component)]
pub struct WorldTerrain;
#[derive(Component)]
pub struct WorldFoliage;
#[derive(Clone, Debug)]
pub struct Placement {
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
    pub holes: Vec<bool>,
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
                    out[layer].uvs.push([wx / 8., wz / 8.]);
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
            for gz in 0..257 {
                for gx in 0..257 {
                    let i = (((256 - gz).min(255)) * 256 + gx.min(255)) * 8;
                    patch.layers[gz * 257 + gx] =
                        (0..8).max_by_key(|n| data[i + n]).unwrap_or(2) as u8;
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
    Ok(result)
}

#[derive(Clone)]
pub struct ObjectCollider {
    pub min: Vec3,
    pub max: Vec3,
    pub transform: Mat4,
    pub inverse: Mat4,
    pub geometry: Arc<ObjData>,
}
#[derive(Resource, Default)]
pub struct WorldGeometry {
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
        min,
        max,
        transform: matrix,
        inverse: matrix.inverse(),
        geometry: data,
    }
}

#[derive(Deserialize)]
struct ProceduralRegion {
    #[serde(alias = "Cells")]
    cells: usize,
    #[serde(alias = "Spacing")]
    spacing: f32,
    #[serde(alias = "Heights")]
    heights: Vec<f32>,
    #[serde(default, alias = "Layers")]
    layers: Vec<u8>,
    #[serde(default, alias = "Holes")]
    holes: Option<Vec<bool>>,
    #[serde(default)]
    origin_x: f32,
    #[serde(default)]
    origin_z: f32,
    #[serde(default, alias = "Trees")]
    trees: Vec<ProceduralTree>,
}
#[derive(Deserialize)]
struct ProceduralTree {
    #[serde(alias = "X")]
    x: f32,
    #[serde(alias = "Y")]
    y: f32,
    #[serde(alias = "Z")]
    z: f32,
    #[serde(alias = "Yaw")]
    yaw: f32,
    #[serde(alias = "Scale")]
    scale: f32,
    #[serde(alias = "Kind")]
    kind: u8,
    #[serde(alias = "Variant")]
    variant: u8,
}
fn read_procedural(path: &Path) -> Result<(TerrainPatch, Vec<ProceduralTree>), ContentError> {
    let data: ProceduralRegion =
        serde_json::from_slice(&fs::read(path)?).map_err(|e| ContentError(e.to_string()))?;
    let count = (data.cells + 1)
        .checked_mul(data.cells + 1)
        .ok_or_else(|| ContentError("terrain size overflow".into()))?;
    if data.cells == 0
        || data.cells > 1024
        || data.heights.len() != count
        || data.spacing <= 0.
        || !data.spacing.is_finite()
        || data.heights.iter().any(|h| !h.is_finite())
    {
        return Err(ContentError("invalid procedural height grid".into()));
    }
    Ok((
        TerrainPatch {
            origin: Vec2::new(data.origin_x, data.origin_z),
            cells: data.cells,
            spacing: data.spacing,
            heights: data.heights,
            layers: data.layers,
            holes: data.holes.unwrap_or_default(),
        },
        data.trees,
    ))
}

fn build_world(
    mut commands: Commands,
    config: Res<WorldConfig>,
    mut state: ResMut<WorldState>,
    mut geometry: ResMut<WorldGeometry>,
    mut cache: ResMut<ContentCache>,
    assets: Res<AssetServer>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    existing: Query<Entity, Or<(With<WorldObject>, With<WorldTerrain>, With<WorldFoliage>)>>,
) {
    for entity in &existing {
        commands.entity(entity).despawn();
    }
    geometry.terrain.clear();
    geometry.objects.clear();
    *state = WorldState::default();
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
    let mut proc_trees = Vec::new();
    if let Some(path) = &config.procedural_path {
        match read_procedural(path) {
            Ok((patch, trees)) => {
                geometry.terrain.push(patch);
                proc_trees = trees;
                state.description = "Original procedural terrain generator".into();
            }
            Err(e) => state.missing_assets.push(e.to_string()),
        }
    } else if let Some(root) = &config.map_root {
        match load_retail_terrain(root) {
            Ok(patches) => {
                geometry.terrain = patches;
                state.description = format!("{} — original retail terrain", config.map_name);
            }
            Err(e) => state.missing_assets.push(e.to_string()),
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
    if let Some(sea) = state.sea_level {
        let mesh = meshes.add(Plane3d::default().mesh().size(8192., 8192.));
        let material = materials.add(StandardMaterial {
            base_color: Color::srgba(0.10, 0.29, 0.36, 0.68),
            alpha_mode: AlphaMode::Blend,
            perceptual_roughness: 0.18,
            reflectance: 0.7,
            double_sided: true,
            cull_mode: None,
            ..default()
        });
        commands.spawn((
            Name::new("Legacy ocean"),
            WorldTerrain,
            Mesh3d(mesh),
            MeshMaterial3d(material),
            Transform::from_xyz(0., sea, 0.),
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
        Vec::new()
    } else {
        let path = config
            .content_root
            .join(format!("objects/placements{suffix}.txt"));
        match fs::read_to_string(path)
            .map_err(ContentError::from)
            .and_then(|t| parse_placements(&t, &names))
        {
            Ok(p) => p,
            Err(e) => {
                state.missing_assets.push(e.to_string());
                Vec::new()
            }
        }
    };
    let center = config.spawn_center.unwrap_or_else(|| {
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
        for p in &placements {
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
    });
    state.focus = center;
    let player_spawn = if config.spawn_center.is_none() && config.procedural_path.is_none() {
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
    let (spawn_point, yaw) = player_spawn.unwrap_or((center, 0.));
    state.spawn = Vec3::new(
        spawn_point.x,
        geometry
            .terrain_height(spawn_point.x, spawn_point.z)
            .unwrap_or(spawn_point.y)
            + 0.5,
        spawn_point.z,
    );
    state.spawn_yaw = yaw;
    let render_radius = config
        .spawn_radius
        .max(center.xz().distance(state.spawn.xz()) + 48.);
    if !state.terrain_available {
        state.description = format!(
            "{} extracted prop viewer — retail terrain unavailable",
            config.map_name
        );
    }
    for patch in &geometry.terrain {
        for (layer, data) in patch
            .mesh_by_layer(center, render_radius + 48.)
            .into_iter()
            .enumerate()
        {
            if data.positions.is_empty() {
                continue;
            }
            let texture = format!("{terrain_dir}/layer{layer}.png");
            let material = cache.material(Some(&texture), false, &assets, &mut materials);
            let shape = trimesh_collider(&data);
            let mut entity = commands.spawn((
                Name::new(format!("Terrain layer {layer}")),
                Mesh3d(meshes.add(data.mesh())),
                MeshMaterial3d(material),
                WorldTerrain,
            ));
            if let Some(shape) = shape {
                entity.insert((RigidBody::Fixed, shape));
            }
        }
    }
    let within = |p: Vec3| Vec2::new(p.x - center.x, p.z - center.z).length() <= render_radius;
    for p in placements
        .into_iter()
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
        let shape = if solid { trimesh_collider(&data) } else { None };
        if solid {
            geometry.objects.push(collider(data, p.transform));
        }
        let mut entity = commands.spawn((
            Name::new(p.name.clone()),
            Mesh3d(mesh),
            MeshMaterial3d(material),
            p.transform,
            WorldObject {
                guid: p.guid,
                asset_name: p.name,
            },
        ));
        if let Some(shape) = shape {
            entity.insert((RigidBody::Fixed, shape));
        }
        state.object_count += 1;
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
                let Ok(bytes) = fs::read(
                    config
                        .content_root
                        .join(resource_dir)
                        .join(format!("{}.bin", f[0])),
                ) else {
                    continue;
                };
                let Ok(transforms) = decode_resource_instances(&bytes) else {
                    continue;
                };
                for mut transform in transforms.into_iter().filter(|t| within(t.translation)) {
                    if is_tree(f[0]) {
                        transform.translation.y -= 0.5 * transform.scale.y.abs();
                        let radius = tree_radius(f[0])
                            * transform.scale.x.abs().max(transform.scale.z.abs());
                        commands.spawn((
                            Name::new(format!("{} trunk", f[0])),
                            WorldFoliage,
                            RigidBody::Fixed,
                            Collider::cylinder(4. * transform.scale.y.abs(), radius),
                            Transform::from_translation(
                                transform.translation + Vec3::Y * 2.5 * transform.scale.y.abs(),
                            ),
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
                let Ok(bytes) = fs::read(&bin) else {
                    continue;
                };
                let Ok(instances) = decode_foliage_instances(&bytes) else {
                    continue;
                };
                let mut merged = ObjData::default();
                for (mut transform, _) in instances {
                    // Match the original Ultra foliage range (five 32m tiles).
                    if transform
                        .translation
                        .xz()
                        .distance(state.spawn.xz())
                        .min(transform.translation.xz().distance(center.xz()))
                        > 160.
                    {
                        continue;
                    }
                    if name.starts_with("pebble") {
                        transform.scale *= 0.75;
                    }
                    let matrix = transform.to_matrix();
                    let normal_matrix = matrix.inverse().transpose();
                    for i in 0..source.positions.len() {
                        merged.positions.push(
                            matrix
                                .transform_point3(Vec3::from_array(source.positions[i]))
                                .to_array(),
                        );
                        merged.normals.push(
                            normal_matrix
                                .transform_vector3(Vec3::from_array(source.normals[i]))
                                .normalize_or_zero()
                                .to_array(),
                        );
                        merged.uvs.push(source.uvs[i]);
                        merged.colors.push(source.colors[i]);
                    }
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
                    Mesh3d(meshes.add(merged.mesh())),
                    MeshMaterial3d(material),
                ));
            }
        }
    }
    if let Some(patch) = geometry
        .terrain
        .first()
        .filter(|_| config.procedural_path.is_some())
    {
        for tree in proc_trees {
            let kind = match tree.kind {
                0 => "Pine",
                1 => "Birch",
                2 => "Maple",
                3 => "Redwood",
                _ => continue,
            };
            let name = format!("{kind}_{}", tree.variant);
            let transform = placement_transform(
                Vec3::new(patch.origin.x + tree.x, tree.y, -(patch.origin.y + tree.z)),
                Vec3::new(270., tree.yaw, 0.),
                Vec3::splat(tree.scale),
            );
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
                ));
                state.foliage_count += 1;
            }
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
