//! Original Nameplate: ordinary world depth, camera-facing image/text and 60m range.
pub(crate) mod raster;
pub(crate) use raster::OriginalFont;
use bevy::{
    asset::{RenderAssetUsages, embedded_asset},
    camera::visibility::{NoFrustumCulling, VisibilitySystems},
    image::{CompressedImageFormats, ImageSampler, ImageType},
    light::{NotShadowCaster, NotShadowReceiver},
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::{AsBindGroup, Extent3d, TextureDimension, TextureFormat},
    shader::ShaderRef,
    text::TextPipeline,
    transform::TransformSystems,
};

pub(super) struct NameplatePlugin;
#[derive(Resource)]
struct BillboardShaderRegistered;

/// Register before either material plugin requests this shared shader. Asset
/// requests can start during plugin build, before CharacterPlugin is installed.
pub(crate) fn ensure_billboard_shader(app: &mut App) {
    if app.world().contains_resource::<BillboardShaderRegistered>() {
        return;
    }
    embedded_asset!(app, "nameplate/billboard.wesl");
    app.insert_resource(BillboardShaderRegistered);
}
impl Plugin for NameplatePlugin {
    fn build(&self, app: &mut App) {
        // Character/physics harnesses intentionally omit the text plugin. Do
        // not require a UI renderer there, or replace its fonts when present.
        if !app.world().contains_resource::<Assets<Font>>() {
            app.init_asset::<Font>();
        }
        ensure_billboard_shader(app);
        app.add_plugins(MaterialPlugin::<PlateMaterial>::default())
            .init_resource::<raster::OriginalFont>()
            .add_systems(Update, rebuild.run_if(resource_exists::<TextPipeline>))
            .add_systems(
                PostUpdate,
                range
                    .after(TransformSystems::Propagate)
                    .before(VisibilitySystems::VisibilityPropagate),
            );
    }
}

#[derive(Component, Clone, PartialEq)]
#[require(Transform, Visibility)]
pub(crate) struct Nameplate {
    /// Already sanitised with original ProfileRules. Never parsed as markup.
    pub name: String,
    /// None is no avatar; corrupt non-null bytes use missing_texture instead.
    pub picture: Option<Handle<Image>>,
}
/// NPC look focus is an additional gate; the range check must not reopen it.
#[derive(Component)]
pub(crate) struct NameplateEnabled(pub bool);
impl Nameplate {
    pub const HEIGHT: f32 = 2.05;
    pub fn above_gear(top_local_y: f32) -> f32 {
        Self::HEIGHT.max(top_local_y + 0.08 + 0.07)
    }
}
#[derive(Component)]
struct Built {
    value: Nameplate,
    parts: Vec<Entity>,
}
#[derive(Asset, AsBindGroup, TypePath, Clone, Debug)]
pub(crate) struct Billboard {}
impl MaterialExtension for Billboard {
    fn vertex_shader() -> ShaderRef {
        "embedded://unturned_bevy/character/nameplate/billboard.wesl".into()
    }
}
pub(crate) type PlateMaterial = ExtendedMaterial<StandardMaterial, Billboard>;

fn rebuild(
    mut commands: Commands,
    plates: Query<(Entity, &Nameplate, Option<&Built>)>,
    mut raster: raster::Raster,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<PlateMaterial>>,
) {
    for (root, plate, old) in &plates {
        if old.is_some_and(|b| b.value == *plate) {
            continue;
        }
        let Some(bitmap) = raster.render(&plate.name) else {
            continue;
        };
        if let Some(old) = old {
            for entity in &old.parts {
                commands.entity(*entity).try_despawn();
            }
        }
        let dimensions =
            Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32) * (0.16 / 64.);
        let label_image = raster.images.add(bitmap.image);
        let mut parts = vec![quad(
            &mut commands,
            &mut meshes,
            &mut materials,
            root,
            label_image,
            dimensions,
            bitmap.offset.extend(0.),
        )];
        if let Some(picture) = &plate.picture {
            parts.push(quad(
                &mut commands,
                &mut meshes,
                &mut materials,
                root,
                picture.clone(),
                Vec2::splat(0.42),
                Vec3::Y * (0.42 * 0.75),
            ));
        }
        commands.entity(root).insert(Built {
            value: plate.clone(),
            parts,
        });
    }
}
fn quad(
    commands: &mut Commands,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<PlateMaterial>,
    root: Entity,
    image: Handle<Image>,
    size: Vec2,
    position: Vec3,
) -> Entity {
    commands
        .spawn((
            Name::new("Original world nameplate surface"),
            Mesh3d(meshes.add(Rectangle::from_size(size))),
            MeshMaterial3d(materials.add(ExtendedMaterial {
                base: StandardMaterial {
                    base_color_texture: Some(image),
                    alpha_mode: AlphaMode::Blend,
                    unlit: true,
                    double_sided: true,
                    cull_mode: None,
                    ..default()
                },
                extension: Billboard {},
            })),
            Transform::from_translation(position),
            ChildOf(root),
            NotShadowCaster,
            NotShadowReceiver,
            NoFrustumCulling,
        ))
        .id()
}
fn range(
    camera: Query<&GlobalTransform, With<crate::player::PlayerCamera>>,
    mut plates: Query<(&GlobalTransform, &mut Visibility, Option<&NameplateEnabled>), With<Nameplate>>,
) {
    let Ok(camera) = camera.single() else {
        return;
    };
    for (pose, mut visibility, enabled) in &mut plates {
        *visibility = if enabled.is_none_or(|e| e.0)
            && pose.translation().distance_squared(camera.translation()) <= 60. * 60. {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
    }
}

fn rgba_image(data: Vec<u8>, width: u32, height: u32) -> Image {
    let (data, levels) = crate::content::mip_chain(data, width as usize, height as usize);
    let mut image = Image::new_uninit(
        Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.texture_descriptor.mip_level_count = levels;
    image.data = Some(data);
    image.sampler = ImageSampler::linear();
    image
}
pub(crate) fn decode_picture(png: &[u8], avatar_pixels: u32) -> Image {
    if let Ok(image) = Image::from_buffer(
        png,
        ImageType::Extension("png"),
        CompressedImageFormats::NONE,
        true,
        ImageSampler::linear(),
        RenderAssetUsages::default(),
    ) {
        if image.width() == avatar_pixels && image.height() == avatar_pixels {
            if let Some(mut rgba) = image.convert(TextureFormat::Rgba8UnormSrgb) {
                if let Some(data) = rgba.data.take() {
                    return rgba_image(data, avatar_pixels, avatar_pixels);
                }
            }
        }
    }
    // Source accepts a validated PNG header before the renderer decodes pixels.
    // A failed non-null image must remain visibly different from no picture.
    let size = avatar_pixels.clamp(8, 512);
    let cell = size / 8;
    let mut data = Vec::with_capacity((size * size * 4) as usize);
    for y in 0..size {
        for x in 0..size {
            data.extend(if (x / cell + y / cell) % 2 == 0 {
                [255, 0, 255, 255]
            } else {
                [13, 13, 13, 255]
            });
        }
    }
    rgba_image(data, size, size)
}

#[cfg(test)]
mod tests;
