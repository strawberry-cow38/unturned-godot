//! Runtime readers for the repository's extracted content. No Godot import cache is used.
//!
//! Both `ContentProvider.ParseObj` (.txt meshes) and `ObjMesh.Load` (.obj meshes) are
//! represented here. Existing coordinates are retained; map placements perform their
//! own coordinate conversion. Bevy uses CCW front faces, so winding is reconciled
//! against authored normals instead of copying Godot's clockwise convention.
use bevy::{
    asset::RenderAssetUsages,
    image::{
        CompressedImageFormats, ImageAddressMode, ImageFilterMode, ImageLoaderSettings,
        ImageSampler, ImageSamplerDescriptor, ImageType,
    },
    prelude::*,
    render::render_resource::PrimitiveTopology,
};
use std::{
    collections::{HashMap, HashSet},
    fmt, fs,
    path::{Path, PathBuf},
    sync::Arc,
};

pub struct ContentPlugin;
impl Plugin for ContentPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<ContentCache>()
            .add_systems(Update, build_pending_mipmaps);
    }
}

#[derive(Debug, Clone)]
pub struct ContentError(pub String);
impl fmt::Display for ContentError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        self.0.fmt(f)
    }
}
impl std::error::Error for ContentError {}
impl From<std::io::Error> for ContentError {
    fn from(e: std::io::Error) -> Self {
        Self(e.to_string())
    }
}

#[derive(Clone, Debug, Default)]
pub struct ObjData {
    pub positions: Vec<[f32; 3]>,
    pub normals: Vec<[f32; 3]>,
    pub uvs: Vec<[f32; 2]>,
    pub colors: Vec<[f32; 4]>,
    pub min: Vec3,
    pub max: Vec3,
}
impl ObjData {
    /// Original ParseObjSplitByZone: all three corners must lie in one zone.
    /// Triangles spanning separate zones stay outside, retaining authored attributes.
    pub fn split_zones(&self, zones: &[(Vec3, Vec3)]) -> (Self, Self) {
        self.split_where(|triangle| {
            zones.iter().any(|(min, max)| {
                triangle
                    .iter()
                    .all(|p| p.cmpge(*min).all() && p.cmple(*max).all())
            })
        })
    }
    /// Source SplitMeshBy, returning (outside, inside) without changing triangle
    /// winding or attributes. The predicate receives all three local corners.
    pub fn split_where(&self, predicate: impl Fn([Vec3; 3]) -> bool) -> (Self, Self) {
        let empty = || Self {
            min: Vec3::splat(f32::INFINITY),
            max: Vec3::splat(f32::NEG_INFINITY),
            ..Default::default()
        };
        let (mut outside, mut inside) = (empty(), empty());
        for (n, triangle) in self.positions.chunks_exact(3).enumerate() {
            let enclosed = predicate([0, 1, 2].map(|i| Vec3::from_array(triangle[i])));
            let out = if enclosed { &mut inside } else { &mut outside };
            for i in n * 3..n * 3 + 3 {
                let p = Vec3::from_array(self.positions[i]);
                out.positions.push(self.positions[i]);
                out.normals.push(self.normals[i]);
                out.uvs.push(self.uvs[i]);
                out.colors.push(self.colors[i]);
                out.min = out.min.min(p);
                out.max = out.max.max(p);
            }
        }
        (outside, inside)
    }
    /// Merge instances without changing source UVs or lighting normals. Reflected
    /// instances reverse winding so their outward surface remains front-facing.
    pub fn append_transformed(&mut self, source: &Self, transform: Transform) {
        let matrix = transform.to_matrix();
        let normal = matrix.inverse().transpose();
        let mirrored = matrix.determinant() < 0.;
        if self.positions.is_empty() {
            self.min = Vec3::splat(f32::INFINITY);
            self.max = Vec3::splat(f32::NEG_INFINITY);
        }
        for tri in (0..source.positions.len()).step_by(3) {
            for corner in if mirrored { [0, 2, 1] } else { [0, 1, 2] } {
                let i = tri + corner;
                let p = matrix.transform_point3(Vec3::from_array(source.positions[i]));
                self.positions.push(p.to_array());
                self.normals.push(
                    normal
                        .transform_vector3(Vec3::from_array(source.normals[i]))
                        .normalize_or_zero()
                        .to_array(),
                );
                self.uvs.push(source.uvs[i]);
                self.colors.push(source.colors[i]);
                self.min = self.min.min(p);
                self.max = self.max.max(p);
            }
        }
    }
    pub fn mesh(&self) -> Mesh {
        Mesh::new(
            PrimitiveTopology::TriangleList,
            RenderAssetUsages::default(),
        )
        .with_inserted_attribute(Mesh::ATTRIBUTE_POSITION, self.positions.clone())
        .with_inserted_attribute(Mesh::ATTRIBUTE_NORMAL, self.normals.clone())
        .with_inserted_attribute(Mesh::ATTRIBUTE_UV_0, self.uvs.clone())
        .with_inserted_attribute(Mesh::ATTRIBUTE_COLOR, self.colors.clone())
    }
    pub fn load(path: &Path) -> Result<Self, ContentError> {
        parse_obj(&fs::read_to_string(path)?)
            .map_err(|e| ContentError(format!("{}: {e}", path.display())))
    }
}

/// Wavefront parsing with independent position/UV/normal indices, negative indices,
/// polygon fan triangulation, optional vertex RGB, and the original V inversion.
pub fn parse_obj(source: &str) -> Result<ObjData, ContentError> {
    let mut vertices = Vec::<Vec3>::new();
    let mut normals = Vec::<Vec3>::new();
    let mut uvs = Vec::<[f32; 2]>::new();
    let mut colors = Vec::<[f32; 4]>::new();
    let mut out = ObjData {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
        ..default()
    };
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
        let err = |s: &str| ContentError(format!("line {}: {s}", line_no + 1));
        let number = |i: usize| -> Result<f32, ContentError> {
            let n = fields
                .get(i)
                .ok_or_else(|| err("missing numeric field"))?
                .parse::<f32>()
                .map_err(|_| err("invalid number"))?;
            if n.is_finite() {
                Ok(n)
            } else {
                Err(err("non-finite number"))
            }
        };
        match fields[0] {
            "v" => {
                vertices.push(Vec3::new(number(1)?, number(2)?, number(3)?));
                colors.push(if fields.len() >= 7 {
                    [number(4)?, number(5)?, number(6)?, 1.]
                } else {
                    [1.; 4]
                });
            }
            "vn" => normals.push(Vec3::new(number(1)?, number(2)?, number(3)?)),
            "vt" => uvs.push([number(1)?, 1. - number(2)?]),
            "f" => {
                if fields.len() < 4 {
                    return Err(err("face has fewer than three corners"));
                }
                let mut corners = Vec::new();
                for field in &fields[1..] {
                    let p: Vec<_> = field.split('/').collect();
                    let index = |text: &str, len: usize| -> Result<usize, ContentError> {
                        let n = text.parse::<i64>().map_err(|_| err("invalid face index"))?;
                        let i = if n > 0 {
                            n - 1
                        } else if n < 0 {
                            len as i64 + n
                        } else {
                            -1
                        };
                        if i < 0 || i >= len as i64 {
                            Err(err("face index outside array"))
                        } else {
                            Ok(i as usize)
                        }
                    };
                    let v = index(p[0], vertices.len())?;
                    let uv = p
                        .get(1)
                        .filter(|s| !s.is_empty() && !uvs.is_empty())
                        .map(|s| index(s, uvs.len()))
                        .transpose()?;
                    let n = p
                        .get(2)
                        .filter(|s| !s.is_empty())
                        .map(|s| index(s, normals.len()))
                        .transpose()?;
                    corners.push((v, uv, n));
                }
                for i in 1..corners.len() - 1 {
                    let mut tri = [corners[0], corners[i], corners[i + 1]];
                    let geometric = (vertices[tri[1].0] - vertices[tri[0].0])
                        .cross(vertices[tri[2].0] - vertices[tri[0].0]);
                    if geometric.length_squared() < 1e-20 {
                        continue;
                    }
                    let authored: Vec3 = tri.iter().filter_map(|v| v.2.map(|n| normals[n])).sum();
                    if geometric.dot(authored) < 0. {
                        tri.swap(1, 2);
                    }
                    let fallback = (vertices[tri[1].0] - vertices[tri[0].0])
                        .cross(vertices[tri[2].0] - vertices[tri[0].0])
                        .normalize();
                    for (v, uv, n) in tri {
                        out.min = out.min.min(vertices[v]);
                        out.max = out.max.max(vertices[v]);
                        out.positions.push(vertices[v].to_array());
                        out.normals.push(
                            n.map(|i| normals[i].try_normalize().unwrap_or(fallback))
                                .unwrap_or(fallback)
                                .to_array(),
                        );
                        out.uvs.push(uv.map(|i| uvs[i]).unwrap_or([0., 0.]));
                        out.colors.push(colors[v]);
                    }
                }
            }
            _ => {}
        }
    }
    if out.positions.is_empty() {
        return Err(ContentError(
            "mesh contains no nondegenerate triangles".into(),
        ));
    }
    Ok(out)
}

#[derive(Resource, Default)]
pub struct ContentCache {
    pub geometry: HashMap<PathBuf, Arc<ObjData>>,
    pub meshes: HashMap<PathBuf, Handle<Mesh>>,
    pub materials: HashMap<String, Handle<StandardMaterial>>,
    alpha_cutouts: HashMap<PathBuf, bool>,
    pending_mipmaps: HashSet<AssetId<Image>>,
}
impl ContentCache {
    pub fn load_mesh(
        &mut self,
        path: &Path,
        meshes: &mut Assets<Mesh>,
    ) -> Result<(Handle<Mesh>, Arc<ObjData>), ContentError> {
        if let (Some(handle), Some(data)) = (self.meshes.get(path), self.geometry.get(path)) {
            return Ok((handle.clone(), data.clone()));
        }
        let data = Arc::new(ObjData::load(path)?);
        let handle = meshes.add(data.mesh());
        self.geometry.insert(path.into(), data.clone());
        self.meshes.insert(path.into(), handle.clone());
        Ok((handle, data))
    }
    /// Same >1% transparent-pixel criterion as WorldBuilder.MatFor. Decode once
    /// for metadata; AssetServer still owns the actual asynchronous GPU asset.
    pub fn has_cutout_alpha(&mut self, path: &Path) -> bool {
        if let Some(value) = self.alpha_cutouts.get(path) {
            return *value;
        }
        let value = fs::read(path)
            .ok()
            .and_then(|bytes| {
                Image::from_buffer(
                    &bytes,
                    ImageType::Extension("png"),
                    CompressedImageFormats::NONE,
                    true,
                    ImageSampler::Default,
                    RenderAssetUsages::MAIN_WORLD,
                )
                .ok()
            })
            .is_some_and(|image| {
                matches!(
                    image.texture_descriptor.format,
                    bevy::render::render_resource::TextureFormat::Rgba8Unorm
                        | bevy::render::render_resource::TextureFormat::Rgba8UnormSrgb
                ) && image.data.as_ref().is_some_and(|data| {
                    data.chunks_exact(4).filter(|p| p[3] < 200).count() > data.len() / 400
                })
            });
        self.alpha_cutouts.insert(path.into(), value);
        value
    }
    /// AssetServer must use the same content root as WorldConfig (AssetPlugin.file_path).
    pub fn material(
        &mut self,
        texture: Option<&str>,
        alpha_cutout: bool,
        assets: &AssetServer,
        materials: &mut Assets<StandardMaterial>,
    ) -> Handle<StandardMaterial> {
        let key = format!("{}|{alpha_cutout}", texture.unwrap_or(""));
        if let Some(m) = self.materials.get(&key) {
            return m.clone();
        }
        let base_color_texture = texture.map(|path| {
            // Source Terrain, FoliageField and ResourceField generate mipmaps and
            // explicitly retain linear filtering; palette props remain nearest.
            let linear = path.starts_with("terrain")
                || path.starts_with("veh_smoke")
                || path == "veh_fire.png"
                || (path.starts_with("foliage")
                    && !path
                        .rsplit('/')
                        .next()
                        .is_some_and(|p| p.starts_with("pebble")))
                || path.starts_with("resources");
            let mipmaps = linear || path.starts_with("foliage");
            let handle = assets
                .load_builder()
                .with_settings(move |settings: &mut ImageLoaderSettings| {
                    settings.sampler = ImageSampler::Descriptor(ImageSamplerDescriptor {
                        address_mode_u: ImageAddressMode::Repeat,
                        address_mode_v: ImageAddressMode::Repeat,
                        mag_filter: if linear {
                            ImageFilterMode::Linear
                        } else {
                            ImageFilterMode::Nearest
                        },
                        min_filter: if linear {
                            ImageFilterMode::Linear
                        } else {
                            ImageFilterMode::Nearest
                        },
                        mipmap_filter: if linear {
                            ImageFilterMode::Linear
                        } else {
                            ImageFilterMode::Nearest
                        },
                        ..default()
                    });
                })
                .load(path.to_owned());
            if mipmaps {
                self.pending_mipmaps.insert(handle.id());
            }
            handle
        });
        let material = materials.add(StandardMaterial {
            base_color_texture,
            perceptual_roughness: 0.95,
            alpha_mode: if alpha_cutout {
                AlphaMode::Mask(0.4)
            } else {
                AlphaMode::Opaque
            },
            double_sided: alpha_cutout,
            cull_mode: if alpha_cutout {
                None
            } else {
                Some(bevy::render::render_resource::Face::Back)
            },
            ..default()
        });
        self.materials.insert(key, material.clone());
        material
    }
}

/// Generate the source's 2×2 RGBA box mip chain. Bevy's PNG loader keeps only
/// level zero; setting a mip sampler alone cannot prevent minification aliasing.
fn build_pending_mipmaps(mut cache: ResMut<ContentCache>, mut images: ResMut<Assets<Image>>) {
    cache.pending_mipmaps.retain(|id| {
        let Some(mut image) = images.get_mut(*id) else {
            return true;
        };
        if image.texture_descriptor.mip_level_count > 1 {
            return false;
        }
        if let Some(mut rgba) =
            image.convert(bevy::render::render_resource::TextureFormat::Rgba8UnormSrgb)
        {
            rgba.sampler = image.sampler.clone();
            let Some(data) = rgba.data.take() else {
                return false;
            };
            let (data, levels) = mip_chain(data, rgba.width() as usize, rgba.height() as usize);
            rgba.data = Some(data);
            rgba.texture_descriptor.mip_level_count = levels;
            *image = rgba;
        }
        false
    });
}
pub(crate) fn mip_chain(mut level: Vec<u8>, mut width: usize, mut height: usize) -> (Vec<u8>, u32) {
    let mut all = level.clone();
    let mut levels = 1;
    while width > 1 || height > 1 {
        let (w, h) = ((width / 2).max(1), (height / 2).max(1));
        let mut next = vec![0; w * h * 4];
        for y in 0..h {
            for x in 0..w {
                for channel in 0..4 {
                    let mut total = 0_u16;
                    for oy in 0..2 {
                        for ox in 0..2 {
                            total += level[(((y * 2 + oy).min(height - 1) * width
                                + (x * 2 + ox).min(width - 1))
                                * 4)
                                + channel] as u16;
                        }
                    }
                    next[(y * w + x) * 4 + channel] = (total / 4) as u8;
                }
            }
        }
        all.extend_from_slice(&next);
        level = next;
        width = w;
        height = h;
        levels += 1;
    }
    (all, levels)
}

/// Original GUID → extracted basename mapping used by all three bundled retail maps.
pub fn load_guid_map(root: &Path) -> Result<HashMap<String, String>, ContentError> {
    let text = fs::read_to_string(root.join("objects/guid_mesh.txt"))?;
    Ok(text
        .lines()
        .filter_map(|line| {
            let mut fields = line.split_whitespace();
            Some((
                fields.next()?.to_ascii_lowercase(),
                fields.next()?.to_owned(),
            ))
        })
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mipmaps_include_all_levels_and_average_rgba_without_losing_base_pixels() {
        let original = vec![
            0, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 0, 255, 255, 255, 255,
        ];
        let (data, count) = mip_chain(original.clone(), 2, 2);
        assert_eq!(count, 2);
        assert_eq!(&data[..16], &original);
        assert_eq!(&data[16..], &[127; 4]);
        let (data, count) = mip_chain(vec![255; 4 * 8], 1, 8);
        assert_eq!(count, 4);
        assert_eq!(data.len(), (8 + 4 + 2 + 1) * 4);
        assert!(data.iter().all(|v| *v == 255));
    }
    #[test]
    fn preserves_geometry_and_flips_uv_origin() {
        let m =
            parse_obj("v 0 0 2 1 0 0\nv 1 0 2\nv 0 1 2\nvt .25 .75\nvn 0 0 1\nf 1/1/1 2/1/1 3/1/1")
                .unwrap();
        assert_eq!(m.positions[0], [0., 0., 2.]);
        assert_eq!(m.uvs[0], [0.25, 0.25]);
        assert_eq!(m.colors[0], [1., 0., 0., 1.]);
    }
    #[test]
    fn normalizes_both_existing_winding_conventions() {
        for face in ["1//1 2//1 3//1", "1//1 3//1 2//1"] {
            let m = parse_obj(&format!("v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 0 1\nf {face}")).unwrap();
            let p: Vec<_> = m.positions.iter().map(|p| Vec3::from_array(*p)).collect();
            assert!((p[1] - p[0]).cross(p[2] - p[0]).z > 0.);
        }
    }
    #[test]
    fn merged_reflected_instances_keep_outward_normals_and_uv_corners() {
        let source =
            parse_obj("v 0 0 0\nv 1 0 0\nv 0 1 1\nvt 0 0\nvt 1 0\nvt 0 1\nf 1/1 2/2 3/3").unwrap();
        let mut mesh = ObjData::default();
        mesh.append_transformed(
            &source,
            Transform {
                translation: Vec3::new(10., 20., 30.),
                scale: Vec3::new(-2., 3., 4.),
                ..default()
            },
        );
        let p: Vec<_> = mesh
            .positions
            .iter()
            .map(|p| Vec3::from_array(*p))
            .collect();
        let normal = Vec3::from_array(mesh.normals[0]);
        assert!((p[1] - p[0]).cross(p[2] - p[0]).normalize().dot(normal) > 0.9999);
        assert_eq!(mesh.min, Vec3::new(8., 20., 30.));
        assert_eq!(mesh.max, Vec3::new(10., 23., 34.));
        assert_eq!(mesh.uvs, vec![source.uvs[0], source.uvs[2], source.uvs[1]]);
    }
    #[test]
    fn triangulates_negative_index_quad() {
        let m = parse_obj("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf -4 -3 -2 -1").unwrap();
        assert_eq!(m.positions.len(), 6);
        assert_eq!(m.normals[5], [0., 0., 1.]);
    }
    #[test]
    fn rejects_bad_asset_data_without_panicking() {
        for source in ["v NaN 0 0", "v 0 0 0\nf 1 2 3", "v 0 0 0\nf 0 0 0", "v 0 0"] {
            assert!(parse_obj(source).is_err());
        }
    }
    #[test]
    fn reads_real_bundled_mesh_families() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        for relative in [
            "jeep_body.txt",
            "objects/Fence_Wood_0.obj",
            "foliage/grass_00.obj",
            // This untextured source OBJ still writes v/vt/vn corner indices;
            // ObjMesh.Load uses UV zero when the complete vt channel is absent.
            "foliage/pebble_00.obj",
        ] {
            let mesh = ObjData::load(&root.join(relative)).unwrap();
            assert!(!mesh.positions.is_empty());
            assert_eq!(mesh.positions.len(), mesh.normals.len());
        }
    }
}
