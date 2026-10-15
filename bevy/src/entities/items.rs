//! WorldItem.GetModel/BuildReplicaVisual: dropped art is distinct from held art.
pub(super) mod material;
use crate::content::{ContentCache, ObjData, mip_chain};
use bevy::{
    asset::RenderAssetUsages,
    image::{
        CompressedImageFormats, ImageFilterMode, ImageSampler, ImageSamplerDescriptor, ImageType,
    },
    prelude::*,
};
use bevy_rapier3d::prelude::Collider;
use serde::Deserialize;
use std::{
    collections::HashMap,
    path::{Path, PathBuf},
    sync::Arc,
};

#[derive(Clone, Deserialize)]
struct Entry {
    obj: String,
    tex: Option<String>,
    color: Option<Vec<f32>>,
    #[serde(rename = "box")]
    size: [f32; 3],
    center: [f32; 3],
    rounds: Option<Vec<usize>>,
    #[serde(default)]
    metal: bool,
    #[serde(default)]
    translucent: bool,
}
#[derive(Component, Clone)]
pub(super) struct Focus {
    pub center: Vec3,
    pub size: Vec3,
    shape: Collider,
}
impl Focus {
    fn new(center: Vec3, size: Vec3) -> Self {
        let size = size.abs().max(Vec3::splat(0.001)) * 1.15;
        Self {
            center,
            size,
            shape: Collider::cuboid(size.x * 0.5, size.y * 0.5, size.z * 0.5),
        }
    }
    pub fn ray(&self, pose: &Transform, from: Vec3, dir: Vec3, limit: f32) -> Option<f32> {
        let inverse = pose.to_matrix().inverse();
        self.shape.cast_ray(
            self.center,
            Quat::IDENTITY,
            inverse.transform_point3(from),
            inverse.transform_vector3(dir),
            limit,
            true,
        )
    }
}
#[derive(Component)]
pub(super) struct Displayed {
    pub item_id: u16,
    pub amount: u16,
}
/// StickProjectile freezes the ordinary pickup at the accepted contact. Its
/// direction already includes the source normal-offset LookAt correction; a
/// vertical hit instead retains WorldItem.Spawn's original YXZ Euler pose.
pub(super) fn rotation(item: &serde_json::Value, net_id: u32) -> Quat {
    if let Some(direction) =
        crate::player::vector(&item["stuckDirection"]).and_then(Vec3::try_normalize)
    {
        return Transform::IDENTITY.looking_to(direction, Vec3::Y).rotation;
    }
    if let Some(euler) = crate::player::vector(&item["stuckEuler"]) {
        return Quat::from_euler(EulerRot::YXZ, euler.y, euler.x, euler.z);
    }
    Quat::from_euler(
        EulerRot::YXZ,
        ((net_id.wrapping_mul(137)) % 360) as f32 * std::f32::consts::PI / 180.,
        std::f32::consts::FRAC_PI_2,
        0.,
    )
}
#[derive(Clone)]
pub(super) struct Art {
    pub mesh: Handle<Mesh>,
    pub material: Handle<StandardMaterial>,
    pub focus: Focus,
    pub translucent: bool,
}
struct Model {
    full: Art,
    data: Option<Arc<ObjData>>,
    rounds: Vec<usize>,
    partial: HashMap<usize, Handle<Mesh>>,
}
#[derive(Resource, Default)]
pub(super) struct Models {
    root: Option<PathBuf>,
    manifest: HashMap<u16, Entry>,
    models: HashMap<u16, Model>,
    textures: HashMap<String, Handle<Image>>,
}
impl Models {
    #[allow(clippy::too_many_arguments)]
    pub fn art(
        &mut self,
        root: &Path,
        id: u16,
        amount: u16,
        stack: u16,
        rarity: &str,
        cache: &mut ContentCache,
        meshes: &mut Assets<Mesh>,
        materials: &mut Assets<StandardMaterial>,
        images: &mut Assets<Image>,
    ) -> Art {
        if self.root.as_deref() != Some(root) {
            self.manifest = std::fs::read(root.join("items/items_manifest.json"))
                .ok()
                .and_then(|s| serde_json::from_slice(&s).ok())
                .unwrap_or_default();
            self.models.clear();
            self.textures.clear();
            self.root = Some(root.to_owned());
        }
        if !self.models.contains_key(&id) {
            let entry = self.manifest.get(&id);
            let loaded = entry.and_then(|e| {
                cache
                    .load_mesh(&root.join("items").join(&e.obj), meshes)
                    .ok()
                    .map(|m| (e, m))
            });
            let model = if let Some((entry, (mesh, data))) = loaded {
                let texture = entry.tex.as_ref().and_then(|path| {
                    if let Some(handle) = self.textures.get(path) {
                        return Some(handle.clone());
                    }
                    let image = load_image(&root.join("items").join(path))?;
                    let handle = images.add(image);
                    self.textures.insert(path.clone(), handle.clone());
                    Some(handle)
                });
                let color = entry
                    .color
                    .as_ref()
                    .filter(|c| c.len() >= 3)
                    .map(|c| Color::srgb(c[0], c[1], c[2]))
                    .unwrap_or_else(|| rarity_color(rarity));
                let material = materials.add(StandardMaterial {
                    base_color: if texture.is_some() {
                        Color::WHITE
                    } else {
                        color
                    },
                    perceptual_roughness: if texture.is_none() {
                        0.7
                    } else if entry.metal {
                        0.3
                    } else {
                        0.8
                    },
                    // Original "metal" means tight specular, intentionally metallic0.
                    metallic: 0.,
                    base_color_texture: texture,
                    alpha_mode: if entry.translucent {
                        AlphaMode::Blend
                    } else {
                        AlphaMode::Opaque
                    },
                    cull_mode: None,
                    double_sided: true,
                    ..default()
                });
                Model {
                    full: Art {
                        mesh,
                        material,
                        focus: Focus::new(
                            Vec3::from_array(entry.center),
                            Vec3::from_array(entry.size),
                        ),
                        translucent: entry.translucent,
                    },
                    data: Some(data),
                    rounds: entry.rounds.clone().unwrap_or_default(),
                    partial: HashMap::new(),
                }
            } else {
                Model {
                    full: Art {
                        mesh: meshes.add(Cuboid::from_length(0.24)),
                        material: materials.add(StandardMaterial {
                            base_color: rarity_color(rarity),
                            perceptual_roughness: 0.55,
                            ..default()
                        }),
                        focus: Focus::new(Vec3::ZERO, Vec3::splat(0.24)),
                        translucent: false,
                    },
                    data: None,
                    rounds: Vec::new(),
                    partial: HashMap::new(),
                }
            };
            self.models.insert(id, model);
        }
        let model = self.models.get_mut(&id).unwrap();
        let count = visible_rounds(amount, stack, model.rounds.len());
        let mut art = model.full.clone();
        if count > 0 && count < model.rounds.len() {
            let triangle_count = model.rounds[count - 1];
            let handle = model.partial.entry(count).or_insert_with(|| {
                let data = model.data.as_ref().unwrap();
                let n = (triangle_count * 3).min(data.positions.len());
                let mut prefix = (**data).clone();
                prefix.positions.truncate(n);
                prefix.normals.truncate(n);
                prefix.uvs.truncate(n);
                prefix.colors.truncate(n);
                meshes.add(prefix.mesh())
            });
            art.mesh = handle.clone();
        }
        art
    }
}
fn load_image(path: &Path) -> Option<Image> {
    let data = std::fs::read(path).ok()?;
    let mut image = Image::from_buffer(
        &data,
        ImageType::Extension("png"),
        CompressedImageFormats::NONE,
        true,
        ImageSampler::Descriptor(ImageSamplerDescriptor {
            mag_filter: ImageFilterMode::Nearest,
            min_filter: ImageFilterMode::Nearest,
            mipmap_filter: ImageFilterMode::Nearest,
            ..default()
        }),
        RenderAssetUsages::default(),
    )
    .ok()?;
    let mut rgba = image.convert(bevy::render::render_resource::TextureFormat::Rgba8UnormSrgb)?;
    let (bytes, levels) = mip_chain(
        rgba.data.take()?,
        rgba.width() as usize,
        rgba.height() as usize,
    );
    rgba.data = Some(bytes);
    rgba.texture_descriptor.mip_level_count = levels;
    rgba.sampler = image.sampler.clone();
    image = rgba;
    Some(image)
}
fn visible_rounds(amount: u16, stack: u16, rounds: usize) -> usize {
    if rounds <= 1 || stack == 0 || amount == 0 {
        return rounds;
    }
    ((usize::from(amount) * (rounds + 1)) / usize::from(stack)).clamp(1, rounds)
}
fn rarity_color(rarity: &str) -> Color {
    match rarity {
        "uncommon" => Color::srgb(31. / 255., 135. / 255., 31. / 255.),
        "rare" => Color::srgb(75. / 255., 20. / 51., 50. / 51.),
        "epic" => Color::srgb(150. / 255., 75. / 255., 50. / 51.),
        "legendary" => Color::srgb(40. / 51., 10. / 51., 50. / 51.),
        "mythical" => Color::srgb(50. / 51., 10. / 51., 5. / 51.),
        _ => Color::WHITE,
    }
}
#[cfg(test)]
mod tests;
