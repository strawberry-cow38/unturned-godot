//! Shared original human skin/clothing/face/gear presentation for zombies, NPCs and players.
//! Wardrobe IDs come from authority; this layer never rolls or equips inventory.
use crate::{
    content::ContentCache,
    rig::{RigAnimation, RigPlayback, RigSkin},
    world::WorldConfig,
};
use bevy::{
    asset::embedded_asset,
    camera::visibility::VisibilityRange,
    image::{ImageLoaderSettings, ImageSampler},
    light::NotShadowCaster,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::AsBindGroup,
    shader::ShaderRef,
};
use std::collections::HashMap;
pub mod ragdoll;
pub struct CharacterPlugin;
impl Plugin for CharacterPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "character/clothes.wesl");
        app.add_plugins((
            MaterialPlugin::<ClothesMaterial>::default(),
            ragdoll::RagdollPlugin,
        ))
        .init_resource::<Wardrobe>()
        .add_systems(Update, dress.after(RigAnimation));
    }
}
#[derive(Component, Clone)]
pub struct Appearance {
    pub skin: Color,
    pub shirt: i32,
    pub pants: i32,
    pub gear: Vec<i32>,
    pub face: Option<u8>,
    pub wear: Vec2,
    pub first_person_clip: f32,
}
impl Default for Appearance {
    fn default() -> Self {
        Self {
            skin: Color::srgb(0.82, 0.66, 0.52),
            shirt: -1,
            pants: -1,
            gear: Vec::new(),
            face: None,
            wear: Vec2::ZERO,
            first_person_clip: 0.,
        }
    }
}
#[derive(Component)]
struct Dressed {
    parts: Vec<Entity>,
}
#[derive(Clone, Debug)]
pub struct Garment {
    pub slot: String,
    pub albedo: String,
    pub emission: String,
    pub metallic: String,
    pub mesh: String,
    pub offset: Vec3,
}
#[derive(Resource, Default)]
pub struct Wardrobe {
    pub entries: HashMap<i32, Garment>,
    loaded: bool,
}
impl Wardrobe {
    pub fn load(root: &std::path::Path) -> Self {
        let mut out = Self {
            loaded: true,
            ..default()
        };
        let Ok(text) = std::fs::read_to_string(root.join("clothing_content.tsv")) else {
            return out;
        };
        for line in text.lines() {
            let c: Vec<_> = line.split('\t').collect();
            if c.len() < 3 {
                continue;
            }
            let Ok(id) = c[0].parse() else {
                continue;
            };
            let cell = |i| c.get(i).copied().unwrap_or("").to_owned();
            let off: Vec<_> = cell(7).split(',').map(str::parse::<f32>).collect();
            let offset = if off.len() == 3 && off.iter().all(Result::is_ok) {
                Vec3::new(
                    *off[0].as_ref().unwrap(),
                    *off[1].as_ref().unwrap(),
                    *off[2].as_ref().unwrap(),
                )
            } else {
                Vec3::ZERO
            };
            out.entries.insert(
                id,
                Garment {
                    slot: cell(1),
                    albedo: cell(3),
                    emission: cell(4),
                    metallic: cell(5),
                    mesh: cell(6),
                    offset,
                },
            );
        }
        out
    }
}
#[derive(Asset, TypePath, AsBindGroup, Clone, Debug)]
pub struct Clothes {
    #[uniform(100)]
    pub skin: Vec4,
    /// shirt wear, pants wear, first-person camera-distance trim, mirror shirt.
    #[uniform(101)]
    pub settings: Vec4,
    #[uniform(102)]
    pub flags: UVec4,
    #[texture(103)]
    #[sampler(104)]
    pub shirt: Option<Handle<Image>>,
    #[texture(105)]
    #[sampler(106)]
    pub shirt_emission: Option<Handle<Image>>,
    #[texture(107)]
    #[sampler(108)]
    pub shirt_metallic: Option<Handle<Image>>,
    #[texture(109)]
    #[sampler(110)]
    pub pants: Option<Handle<Image>>,
    #[texture(111)]
    #[sampler(112)]
    pub pants_emission: Option<Handle<Image>>,
    #[texture(113)]
    #[sampler(114)]
    pub pants_metallic: Option<Handle<Image>>,
}
impl MaterialExtension for Clothes {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/character/clothes.wesl".into()
    }
    fn deferred_fragment_shader() -> ShaderRef {
        Self::fragment_shader()
    }
}
pub type ClothesMaterial = ExtendedMaterial<StandardMaterial, Clothes>;
fn texture(
    root: &std::path::Path,
    assets: &AssetServer,
    path: &str,
    linear: bool,
) -> Option<Handle<Image>> {
    (!path.is_empty() && root.join(path).is_file()).then(|| {
        assets
            .load_builder()
            .with_settings(move |s: &mut ImageLoaderSettings| {
                s.sampler = ImageSampler::nearest();
                s.is_srgb = !linear;
            })
            .load(path.to_owned())
    })
}
fn clothes(
    appearance: &Appearance,
    wardrobe: &Wardrobe,
    config: &WorldConfig,
    assets: &AssetServer,
) -> ClothesMaterial {
    let garment = |id: i32| -> [Option<Handle<Image>>; 3] {
        wardrobe.entries.get(&id).map_or([None, None, None], |g| {
            [
                texture(&config.content_root, assets, &g.albedo, false),
                texture(&config.content_root, assets, &g.emission, false),
                texture(&config.content_root, assets, &g.metallic, true),
            ]
        })
    };
    let [shirt, shirt_emission, shirt_metallic] = garment(appearance.shirt);
    let [pants, pants_emission, pants_metallic] = garment(appearance.pants);
    let flags = [
        &shirt,
        &shirt_emission,
        &shirt_metallic,
        &pants,
        &pants_emission,
        &pants_metallic,
    ]
    .iter()
    .enumerate()
    .fold(0, |bits, (i, v)| {
        bits | if v.is_some() { 1 << i } else { 0 }
    });
    ExtendedMaterial {
        base: StandardMaterial {
            perceptual_roughness: 1.,
            ..default()
        },
        extension: Clothes {
            skin: appearance.skin.to_linear().to_vec4(),
            settings: Vec4::new(
                appearance.wear.x,
                appearance.wear.y,
                appearance.first_person_clip,
                0.,
            ),
            flags: UVec4::new(flags, 0, 0, 0),
            shirt,
            shirt_emission,
            shirt_metallic,
            pants,
            pants_emission,
            pants_metallic,
        },
    }
}
#[allow(clippy::too_many_arguments)]
fn dress(
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    mut wardrobe: ResMut<Wardrobe>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut clothing: ResMut<Assets<ClothesMaterial>>,
    rigs: Query<
        (
            Entity,
            &Appearance,
            &RigPlayback,
            &RigSkin,
            Option<&Dressed>,
        ),
        Changed<Appearance>,
    >,
) {
    if !wardrobe.loaded {
        *wardrobe = Wardrobe::load(&config.content_root);
    }
    for (root, appearance, rig, skin, previous) in &rigs {
        if let Some(previous) = previous {
            for e in &previous.parts {
                commands.entity(*e).try_despawn();
            }
        }
        let material = clothing.add(clothes(appearance, &wardrobe, &config, &assets));
        commands
            .entity(skin.0)
            .remove::<MeshMaterial3d<StandardMaterial>>()
            .insert(MeshMaterial3d(material.clone()));
        let mut parts = Vec::new();
        if let (Some(face), Some(skull)) = (appearance.face, rig.bones.get("Skull")) {
            let face = face.min(31);
            let path = format!("faces/face_{face}.png");
            let emission = texture(
                &config.content_root,
                &assets,
                &format!("faces/face_{face}_emission.png"),
                false,
            );
            if let Some(texture) = texture(&config.content_root, &assets, &path, false) {
                let face = commands
                    .spawn((
                        Name::new("Original skull face"),
                        Mesh3d(meshes.add(Rectangle::from_size(Vec2::splat(0.38)))),
                        MeshMaterial3d(materials.add(StandardMaterial {
                            base_color_texture: Some(texture),
                            emissive: if emission.is_some() {
                                LinearRgba::rgb(1.5, 1.5, 1.5)
                            } else {
                                LinearRgba::BLACK
                            },
                            emissive_texture: emission,
                            alpha_mode: AlphaMode::Mask(0.5),
                            cull_mode: None,
                            double_sided: true,
                            ..default()
                        })),
                        Transform::from_translation(Vec3::new(-0.43, 0., -0.212)).with_rotation(
                            Quat::from_mat3(&Mat3::from_cols(
                                Vec3::NEG_Y,
                                Vec3::NEG_X,
                                Vec3::NEG_Z,
                            )),
                        ),
                        VisibilityRange {
                            start_margin: 0.0..0.0,
                            end_margin: 45.0..45.0,
                            use_aabb: false,
                        },
                        NotShadowCaster,
                        ChildOf(*skull),
                    ))
                    .id();
                parts.push(face);
            }
        }
        for id in &appearance.gear {
            let Some(entry) = wardrobe.entries.get(id) else {
                continue;
            };
            let bone = match entry.slot.as_str() {
                "hat" | "mask" | "glasses" => "Skull",
                "vest" | "backpack" => "Spine",
                _ => continue,
            };
            let Some(parent) = rig.bones.get(bone) else {
                continue;
            };
            if entry.mesh.is_empty() {
                continue;
            }
            match cache.load_mesh(&config.content_root.join(&entry.mesh), &mut meshes) {
                Ok((mesh, _)) => {
                    let material = materials.add(StandardMaterial {
                        base_color: Srgba::hex(&entry.albedo)
                            .map(Color::Srgba)
                            .unwrap_or(Color::WHITE),
                        base_color_texture: texture(
                            &config.content_root,
                            &assets,
                            &entry.albedo,
                            false,
                        ),
                        cull_mode: None,
                        double_sided: true,
                        ..default()
                    });
                    let gear = commands
                        .spawn((
                            Name::new(format!("{} #{id}", entry.slot)),
                            Mesh3d(mesh),
                            MeshMaterial3d(material),
                            Transform::from_translation(entry.offset),
                            VisibilityRange {
                                start_margin: 0.0..0.0,
                                end_margin: 95.0..95.0,
                                use_aabb: false,
                            },
                            ChildOf(*parent),
                        ))
                        .id();
                    parts.push(gear);
                }
                Err(e) => warn!("Cannot load source clothing {id}: {e}"),
            }
        }
        commands.entity(root).insert(Dressed { parts });
    }
}

#[cfg(test)]
mod tests;
