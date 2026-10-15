//! Terrain.TERRAIN_SHADER: bilinear authored splat weights, world-noise boundary
//! jitter and a single winning material per pixel with sixteen-metre texture tiling.
use super::{ContentError, TerrainPatch, WorldReady};
use crate::{content::mip_chain, player::GameSnapshot};
use bevy::{
    asset::{RenderAssetUsages, embedded_asset},
    image::{
        CompressedImageFormats, ImageAddressMode, ImageFilterMode, ImageSampler,
        ImageSamplerDescriptor, ImageType,
    },
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::{AsBindGroup, Extent3d, TextureDimension, TextureFormat},
    shader::ShaderRef,
};
use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    sync::{Arc, Weak},
};

#[derive(Debug, Clone)]
pub struct SplatField {
    pub origin: Vec2,
    pub size: Vec2,
    pub width: usize,
    pub height: usize,
    pub pixels: Vec<[u8; 8]>,
    /// Retail texture rows were reflected with world Z, so its source UV-space
    /// noise warp must reflect too. Generated grids already use increasing Z.
    pub reflect_noise_z: bool,
}
impl SplatField {
    pub fn retail(data: &[u8], origin: Vec2) -> Result<Self, ContentError> {
        if data.len() != 256 * 256 * 8 {
            return Err(ContentError(
                "retail splat must contain 256² eight-channel texels".into(),
            ));
        }
        let mut pixels = vec![[0; 8]; 256 * 256];
        for z in 0..256 {
            for x in 0..256 {
                let i = ((255 - z) * 256 + x) * 8;
                pixels[z * 256 + x].copy_from_slice(&data[i..i + 8]);
            }
        }
        Ok(Self {
            origin,
            size: Vec2::splat(1024.),
            width: 256,
            height: 256,
            pixels,
            reflect_noise_z: true,
        })
    }
    /// The original retail renderer merges all tile weights before filtering.
    /// Shared texture edges therefore interpolate adjacent tiles without seams.
    pub fn merge_retail(patches: &[TerrainPatch]) -> Self {
        let origin = patches
            .iter()
            .fold(Vec2::splat(f32::INFINITY), |o, p| o.min(p.origin));
        let end = patches.iter().fold(Vec2::splat(f32::NEG_INFINITY), |e, p| {
            e.max(p.origin + Vec2::splat(1024.))
        });
        let size = end - origin;
        let (width, height) = (
            (size.x / 4.).round() as usize,
            (size.y / 4.).round() as usize,
        );
        // Terrain.Load merges missing splat tiles into zero-filled buffers.
        let mut pixels = vec![[0; 8]; width * height];
        for patch in patches {
            if let Some(field) = &patch.splat {
                let offset = (patch.origin - origin) / 4.;
                for z in 0..256 {
                    for x in 0..256 {
                        pixels[(z + offset.y.round() as usize) * width
                            + x
                            + offset.x.round() as usize] = field.pixels[z * 256 + x];
                    }
                }
            }
        }
        Self {
            origin,
            size,
            width,
            height,
            pixels,
            reflect_noise_z: true,
        }
    }
    pub fn for_patch(patch: &TerrainPatch) -> Arc<Self> {
        if let Some(field) = &patch.splat {
            return field.clone();
        }
        let width = patch.cells + 1;
        let mut pixels = vec![[0; 8]; width * width];
        for (i, pixel) in pixels.iter_mut().enumerate() {
            pixel[patch.layers.get(i).copied().unwrap_or(2).min(7) as usize] = 255;
        }
        // RegionStreamer uses (vertex+.5)/(cells+1), so adjacent regions sample
        // identical edge texel centres even though the textures are separate.
        Arc::new(Self {
            origin: patch.origin - Vec2::splat(patch.spacing * 0.5),
            size: Vec2::splat(width as f32 * patch.spacing),
            width,
            height: width,
            pixels,
            reflect_noise_z: false,
        })
    }
}
#[derive(Component)]
pub struct TerrainSurface {
    pub splat: Arc<SplatField>,
    pub root: PathBuf,
    pub directories: [String; 8],
}
pub struct TerrainPlugin;
impl Plugin for TerrainPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "terrain.wesl");
        app.add_plugins(MaterialPlugin::<TerrainMaterial>::default())
            .init_resource::<TextureCache>()
            .add_systems(Update, (convert.after(WorldReady), weather));
    }
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
pub struct TerrainExtension {
    #[uniform(100)]
    pub rect: Vec4,
    #[uniform(101)]
    pub settings: Vec4,
    #[uniform(108)]
    pub boundary: Vec4,
    #[texture(102, dimension = "2d_array")]
    #[sampler(103)]
    pub albedos: Handle<Image>,
    #[texture(104)]
    #[sampler(105)]
    pub splat0: Handle<Image>,
    #[texture(106)]
    #[sampler(107)]
    pub splat1: Handle<Image>,
}
impl MaterialExtension for TerrainExtension {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/terrain.wesl".into()
    }
    fn deferred_fragment_shader() -> ShaderRef {
        Self::fragment_shader()
    }
}
pub(super) type TerrainMaterial = ExtendedMaterial<StandardMaterial, TerrainExtension>;
#[derive(Resource, Default)]
struct TextureCache {
    albedos: HashMap<String, Handle<Image>>,
    splats: HashMap<usize, (Weak<SplatField>, [Handle<Image>; 2])>,
}
fn sampler(repeat: bool) -> ImageSampler {
    ImageSampler::Descriptor(ImageSamplerDescriptor {
        address_mode_u: if repeat {
            ImageAddressMode::Repeat
        } else {
            ImageAddressMode::ClampToEdge
        },
        address_mode_v: if repeat {
            ImageAddressMode::Repeat
        } else {
            ImageAddressMode::ClampToEdge
        },
        min_filter: ImageFilterMode::Linear,
        mag_filter: ImageFilterMode::Linear,
        mipmap_filter: ImageFilterMode::Linear,
        ..default()
    })
}
fn albedo_array(root: &Path, dirs: &[String; 8]) -> Result<Image, ContentError> {
    let mut dimensions = None;
    let mut pixels = Vec::new();
    let mut levels = 1;
    for (layer, dir) in dirs.iter().enumerate() {
        let path = root.join(format!("{dir}/layer{layer}.png"));
        let image = Image::from_buffer(
            &std::fs::read(&path)?,
            ImageType::Extension("png"),
            CompressedImageFormats::NONE,
            true,
            ImageSampler::Default,
            RenderAssetUsages::default(),
        )
        .map_err(|e| ContentError(format!("{}: {e}", path.display())))?
        .convert(TextureFormat::Rgba8UnormSrgb)
        .ok_or_else(|| ContentError("unsupported terrain texture format".into()))?;
        let size = (image.width(), image.height());
        if dimensions.is_some_and(|v| v != size) {
            return Err(ContentError(
                "terrain array layers must have equal dimensions".into(),
            ));
        }
        dimensions = Some(size);
        let (chain, count) = mip_chain(
            image.data.unwrap_or_default(),
            size.0 as usize,
            size.1 as usize,
        );
        pixels.extend(chain);
        levels = count;
    }
    let (width, height) = dimensions.unwrap();
    // Image stores LayerMajor mip data, matching one complete mip chain per layer.
    let mut image = Image::new(
        Extent3d {
            width,
            height,
            depth_or_array_layers: 8,
        },
        TextureDimension::D2,
        vec![0; (width * height * 8 * 4) as usize],
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.data = Some(pixels);
    image.texture_descriptor.mip_level_count = levels;
    image.sampler = sampler(true);
    Ok(image)
}
fn splat_images(field: &SplatField, images: &mut Assets<Image>) -> [Handle<Image>; 2] {
    std::array::from_fn(|part| {
        let data = field
            .pixels
            .iter()
            .flat_map(|p| p[part * 4..part * 4 + 4].iter().copied())
            .collect();
        let mut image = Image::new(
            Extent3d {
                width: field.width as u32,
                height: field.height as u32,
                depth_or_array_layers: 1,
            },
            TextureDimension::D2,
            data,
            TextureFormat::Rgba8Unorm,
            RenderAssetUsages::default(),
        );
        image.sampler = sampler(false);
        images.add(image)
    })
}
fn convert(
    origin: Res<super::origin::WorldOrigin>,
    mut commands: Commands,
    mut materials: ResMut<Assets<TerrainMaterial>>,
    mut images: ResMut<Assets<Image>>,
    mut cache: ResMut<TextureCache>,
    query: Query<(Entity, &TerrainSurface), Without<MeshMaterial3d<TerrainMaterial>>>,
) {
    cache
        .splats
        .retain(|_, (field, _)| field.strong_count() > 0);
    for (entity, surface) in &query {
        let key = surface.directories.join("|");
        if !cache.albedos.contains_key(&key) {
            match albedo_array(&surface.root, &surface.directories) {
                Ok(image) => {
                    cache.albedos.insert(key.clone(), images.add(image));
                }
                Err(error) => {
                    warn!("Terrain material: {error}");
                    commands.entity(entity).remove::<TerrainSurface>();
                    continue;
                }
            }
        }
        let albedos = cache.albedos[&key].clone();
        let id = Arc::as_ptr(&surface.splat) as usize;
        let splats = &cache
            .splats
            .entry(id)
            .or_insert_with(|| {
                (
                    Arc::downgrade(&surface.splat),
                    splat_images(&surface.splat, &mut images),
                )
            })
            .1;
        let extension = TerrainExtension {
            rect: Vec4::new(
                surface.splat.origin.x,
                surface.splat.origin.y,
                surface.splat.size.x,
                surface.splat.size.y,
            ),
            settings: Vec4::new(0.75, 16., 0., 0.),
            boundary: Vec4::new(
                if surface.splat.reflect_noise_z {
                    -1.
                } else {
                    1.
                },
                origin.shader_offset().x,
                origin.shader_offset().y,
                0.,
            ),
            albedos,
            splat0: splats[0].clone(),
            splat1: splats[1].clone(),
        };
        let material = materials.add(ExtendedMaterial {
            base: StandardMaterial {
                perceptual_roughness: 1.,
                ..default()
            },
            extension,
        });
        commands
            .entity(entity)
            .remove::<MeshMaterial3d<StandardMaterial>>()
            .insert(MeshMaterial3d(material));
    }
}
fn weather(snapshot: Res<GameSnapshot>, mut materials: ResMut<Assets<TerrainMaterial>>) {
    let state = &snapshot.0["weather"];
    let snow = state["snowCover"].as_f64().unwrap_or(0.) as f32;
    let wet = state["rainWetness"].as_f64().unwrap_or(0.) as f32;
    for (_, material) in materials.iter_mut() {
        if material.extension.settings.z != snow || material.extension.settings.w != wet {
            material.extension.settings.z = snow;
            material.extension.settings.w = wet;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn retail_splat_retains_all_weights_and_reflects_z_without_transposing_x() {
        let mut bytes = vec![0; 256 * 256 * 8];
        bytes[(17 * 256 + 23) * 8..(17 * 256 + 23) * 8 + 8]
            .copy_from_slice(&[8, 16, 24, 32, 40, 48, 56, 64]);
        let field = SplatField::retail(&bytes, Vec2::new(1024., -2048.)).unwrap();
        assert_eq!(
            field.pixels[(255 - 17) * 256 + 23],
            [8, 16, 24, 32, 40, 48, 56, 64]
        );
        assert_eq!(field.origin, Vec2::new(1024., -2048.));
        assert_eq!(field.size, Vec2::splat(1024.));
        assert!(field.reflect_noise_z);
        assert!(SplatField::retail(&bytes[..100], Vec2::ZERO).is_err());
    }
    #[test]
    fn retail_tile_merge_places_neighbors_in_one_filterable_image() {
        let make = |origin: Vec2, layer: usize| {
            let mut pixels = vec![[0; 8]; 256 * 256];
            for p in &mut pixels {
                p[layer] = 255;
            }
            TerrainPatch {
                origin,
                cells: 256,
                spacing: 4.,
                heights: vec![],
                layers: vec![],
                normals: vec![],
                holes: vec![],
                splat: Some(Arc::new(SplatField {
                    origin,
                    size: Vec2::splat(1024.),
                    width: 256,
                    height: 256,
                    pixels,
                    reflect_noise_z: true,
                })),
            }
        };
        let field =
            SplatField::merge_retail(&[make(Vec2::new(-1024., 0.), 2), make(Vec2::ZERO, 5)]);
        assert_eq!((field.width, field.height), (512, 256));
        assert_eq!(field.pixels[255][2], 255);
        assert_eq!(field.pixels[256][5], 255);
        assert_eq!(field.origin, Vec2::new(-1024., 0.));
    }
    #[test]
    fn procedural_splat_aligns_vertex_samples_to_texel_centers() {
        let patch = TerrainPatch {
            origin: Vec2::new(-256., 512.),
            cells: 2,
            spacing: 128.,
            heights: vec![],
            layers: vec![0, 1, 2, 3, 4, 5, 6, 7, 2],
            normals: vec![],
            holes: vec![],
            splat: None,
        };
        let field = SplatField::for_patch(&patch);
        let first = (patch.origin - field.origin) / field.size;
        let last = (patch.origin + Vec2::splat(256.) - field.origin) / field.size;
        assert!((first - Vec2::splat(0.5 / 3.)).length() < 1e-6);
        assert!((last - Vec2::splat(2.5 / 3.)).length() < 1e-6);
        assert_eq!(field.pixels[4], [0, 0, 0, 0, 255, 0, 0, 0]);
        assert!(!field.reflect_noise_z);
    }
    #[test]
    fn bundled_terrain_array_contains_eight_complete_mip_chains() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let dirs = std::array::from_fn(|layer| {
            if matches!(layer, 0 | 3 | 6 | 7) {
                "terrain_yukon".into()
            } else {
                "terrain".into()
            }
        });
        let image = albedo_array(&root, &dirs).unwrap();
        assert_eq!(image.texture_descriptor.size.depth_or_array_layers, 8);
        assert_eq!(
            image.texture_descriptor.format,
            TextureFormat::Rgba8UnormSrgb
        );
        let (mut width, mut height) = (image.width() as usize, image.height() as usize);
        let mut expected = 0;
        for _ in 0..image.texture_descriptor.mip_level_count {
            expected += width * height * 4 * 8;
            width = (width / 2).max(1);
            height = (height / 2).max(1);
        }
        assert_eq!(image.data.as_ref().unwrap().len(), expected);
        assert_eq!((width, height), (1, 1));
    }
}

pub(super) fn shift_origin(world: &mut World, shift: Vec3, offset: Vec2) {
    for mut surface in world.query::<&mut TerrainSurface>().iter_mut(world) {
        Arc::make_mut(&mut surface.splat).origin -= shift.xz();
    }
    if let Some(mut materials) = world.get_resource_mut::<Assets<TerrainMaterial>>() {
        for (_, material) in materials.iter_mut() {
            material.extension.rect.x -= shift.x;
            material.extension.rect.y -= shift.z;
            material.extension.boundary.y = offset.x;
            material.extension.boundary.z = offset.y;
        }
    }
}
