//! Runtime map art and the original polygon markers. No replacement map is generated.
use super::*;
use bevy::{
    asset::RenderAssetUsages,
    image::{ImageFilterMode, ImageLoaderSettings, ImageSampler, ImageSamplerDescriptor},
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};
use std::{collections::HashSet, path::Path};

#[derive(Resource, Default)]
pub(super) struct MapAssets {
    pub(super) key: String,
    pub overview: Option<Handle<Image>>,
    pub tiles: [Option<Handle<Image>>; 16],
    missing: [bool; 16],
    pending: HashSet<AssetId<Image>>,
    pub towns: Vec<(String, Vec2)>,
    pub arrow: Handle<Image>,
    pub pin: Handle<Image>,
    pub error: String,
}
impl MapAssets {
    pub fn load(&mut self, config: &WorldConfig, server: &AssetServer, images: &mut Assets<Image>) {
        let key = format!("{}|{}", config.content_root.display(), config.map_name);
        if self.key == key {
            return;
        }
        *self = Self {
            key,
            arrow: images.add(polygon(false)),
            pin: images.add(polygon(true)),
            ..default()
        };
        let base = config.map_name.to_ascii_lowercase();
        let baked = config.content_root.join(format!("{base}_map_baked.png"));
        let retail = config.content_root.join(format!("{base}_map.png"));
        let path = if baked.is_file() && std::env::var("UG_MAPRETAIL").as_deref() != Ok("1") {
            baked
        } else {
            retail
        };
        if path.is_file() {
            self.overview = Some(load_image(server, &path));
        } else {
            self.error = format!(
                "Map image unavailable: {}",
                path.file_name().unwrap_or_default().to_string_lossy()
            );
        }
        let nodes = config.nodes_file();
        self.towns = std::fs::read_to_string(config.content_root.join(nodes))
            .map(|s| parse_nodes(&s))
            .unwrap_or_default();
    }
    pub fn tiles(
        &mut self,
        view: &geometry::View,
        side: f32,
        config: &WorldConfig,
        server: &AssetServer,
    ) {
        for i in 0..16 {
            if view.tile_visible(i / 4, i % 4, side) {
                if self.tiles[i].is_none() && !self.missing[i] {
                    let path = config.content_root.join(format!(
                        "{}_map_baked_{}_{}.jpg",
                        config.map_name.to_ascii_lowercase(),
                        i / 4,
                        i % 4
                    ));
                    if path.is_file() {
                        let handle = load_image(server, &path);
                        self.pending.insert(handle.id());
                        self.tiles[i] = Some(handle);
                    } else {
                        self.missing[i] = true;
                    }
                }
            } else if let Some(handle) = self.tiles[i].take() {
                self.pending.remove(&handle.id());
            }
        }
    }
}
fn load_image(server: &AssetServer, path: &Path) -> Handle<Image> {
    server
        .load_builder()
        .with_settings::<ImageLoaderSettings>(|settings| {
            settings.sampler = ImageSampler::Descriptor(ImageSamplerDescriptor {
                mag_filter: ImageFilterMode::Linear,
                min_filter: ImageFilterMode::Linear,
                mipmap_filter: ImageFilterMode::Linear,
                ..default()
            })
        })
        .load(
            path.file_name()
                .expect("map asset filename")
                .to_string_lossy()
                .into_owned(),
        )
}
pub(super) fn mipmaps(mut assets: ResMut<MapAssets>, mut images: ResMut<Assets<Image>>) {
    assets.pending.retain(|id| {
        let Some(mut image) = images.get_mut(*id) else {
            return true;
        };
        if image.texture_descriptor.mip_level_count > 1 {
            return false;
        }
        if let Some(mut rgba) = image.convert(TextureFormat::Rgba8UnormSrgb) {
            rgba.sampler = image.sampler.clone();
            if let Some(data) = rgba.data.take() {
                let (data, count) =
                    crate::content::mip_chain(data, rgba.width() as usize, rgba.height() as usize);
                rgba.data = Some(data);
                rgba.texture_descriptor.mip_level_count = count;
                *image = rgba;
            }
        }
        false
    });
}
pub(super) fn parse_nodes(text: &str) -> Vec<(String, Vec2)> {
    text.lines()
        .filter_map(|line| {
            let mut columns = line.split('\t');
            let name = columns.next()?.to_owned();
            let p: Vec<f32> = columns
                .next()?
                .split(',')
                .map(str::parse)
                .collect::<Result<_, _>>()
                .ok()?;
            (p.len() == 3 && p.iter().all(|n| n.is_finite()))
                .then(|| (name, geometry::normalized(Vec3::new(p[0], p[1], p[2]))))
        })
        .collect()
}
/// Rasterize the original four-vertex polygons and centroid-grown three-pixel
/// halo at 4x resolution, preserving their screen-pixel dimensions under zoom.
fn polygon(pin: bool) -> Image {
    let polygon = if pin {
        [
            Vec2::ZERO,
            Vec2::new(-10., -22.),
            Vec2::new(0., -30.),
            Vec2::new(10., -22.),
        ]
    } else {
        [
            Vec2::new(0., -17.),
            Vec2::new(11., 12.),
            Vec2::new(0., 5.),
            Vec2::new(-11., 12.),
        ]
    };
    let center = polygon.iter().sum::<Vec2>() / 4.;
    let halo = polygon.map(|p| p + (p - center).normalize_or_zero() * 3.);
    let mut data = vec![0; 256 * 256 * 4];
    for y in 0..256 {
        for x in 0..256 {
            let p = Vec2::new(x as f32 + 0.5, y as f32 + 0.5) / 4. - Vec2::splat(32.);
            let color = if inside(p, &polygon) {
                if pin {
                    [255, 217, 51, 255]
                } else {
                    [64, 230, 255, 255]
                }
            } else if inside(p, &halo) {
                [0, 0, 0, 217]
            } else {
                [0; 4]
            };
            data[(y * 256 + x) * 4..(y * 256 + x + 1) * 4].copy_from_slice(&color);
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: 256,
            height: 256,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::linear();
    image
}
fn inside(p: Vec2, polygon: &[Vec2; 4]) -> bool {
    let mut result = false;
    for i in 0..4 {
        let a = polygon[i];
        let b = polygon[(i + 1) % 4];
        if (a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x {
            result = !result;
        }
    }
    result
}
