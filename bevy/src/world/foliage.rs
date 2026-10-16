//! Source foliage wind, shared disturbance field, up-normal lighting and erosion.
mod batch;
mod disturbance;
mod reaction;
mod fall;
mod resources;
mod timber;
pub(super) use resources::ResourcePresentationPlugin;
use crate::world::{
    WorldReady,
    origin::WorldOrigin,
    rain::{RainState, RainUpdate},
};
pub(super) use batch::Batch;
use bevy::{
    asset::embedded_asset,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::{AsBindGroup, ShaderType},
    shader::{ShaderRef, load_shader_library},
    transform::TransformSystems,
};
pub(super) fn shift_origin(world: &mut World, shift: Vec3) {
    disturbance::shift_origin(world, shift);
    resources::shift_origin(world, shift);
}
use std::collections::HashMap;

pub struct FoliagePlugin;
impl Plugin for FoliagePlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "foliage.wesl");
        embedded_asset!(app, "foliage/vertex.wesl");
        embedded_asset!(app, "foliage/prepass.wesl");
        embedded_asset!(app, "foliage/depth.wesl");
        load_shader_library!(app, "foliage/motion.wesl");
        app.add_plugins(MaterialPlugin::<FoliageMaterial>::default())
            .init_resource::<Materials>()
            .init_resource::<disturbance::Field>()
            .add_systems(Update, convert.after(WorldReady).after(RainUpdate))
            .add_systems(
                PostUpdate,
                (disturbance::gather, update, reaction::apply)
                    .chain()
                    .after(TransformSystems::Propagate),
            );
    }
}
#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]
pub enum Kind {
    Static,
    Grass,
    Flowers,
    Canopy,
}
#[derive(Component)]
pub struct FoliageSurface {
    range: f32,
    kind: Kind,
    tint: Option<f32>,
}
impl FoliageSurface {
    pub fn ground(name: &str, range: f32) -> Self {
        let stem = name.rsplit('/').next().unwrap_or(name);
        let kind = if stem.starts_with("grass") {
            Kind::Grass
        } else if stem.starts_with("flowers") {
            Kind::Flowers
        } else if stem.starts_with("Bush") {
            Kind::Canopy
        } else {
            Kind::Static
        };
        let tint = if stem.starts_with("pebble_sand") {
            Some(0.506)
        } else if stem.starts_with("pebble") {
            Some(0.456)
        } else if kind == Kind::Canopy {
            None
        } else {
            Some(1.)
        };
        Self { range, kind, tint }
    }
    pub fn canopy() -> Self {
        Self {
            range: 0.,
            kind: Kind::Canopy,
            tint: None,
        }
    }
}
#[derive(Debug, Clone, Reflect, ShaderType)]
pub struct MotionUniform {
    wind: Vec4,
    origin: Vec4,
    settings: Vec4,
    gust: Vec4,
    base: Vec4,
    positions: [Vec4; disturbance::MAX],
    responses: [Vec4; disturbance::MAX],
}
impl MotionUniform {
    fn new(kind: Kind) -> Self {
        Self {
            wind: Vec4::ZERO,
            origin: Vec4::ZERO,
            settings: Vec4::new(kind as u8 as f32, 0.05, 0., 0.),
            gust: Vec4::ZERO,
            base: Vec4::ZERO,
            positions: [Vec4::ZERO; disturbance::MAX],
            responses: [Vec4::ZERO; disturbance::MAX],
        }
    }
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
pub struct FoliageExtension {
    #[uniform(100)]
    pub fade: Vec4,
    #[uniform(101)]
    pub motion: MotionUniform,
}
impl MaterialExtension for FoliageExtension {
    fn vertex_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/foliage/vertex.wesl".into()
    }
    fn prepass_vertex_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/foliage/prepass.wesl".into()
    }
    fn prepass_fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/foliage/depth.wesl".into()
    }
    fn deferred_vertex_shader() -> ShaderRef {
        Self::prepass_vertex_shader()
    }
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/foliage.wesl".into()
    }
    fn deferred_fragment_shader() -> ShaderRef {
        Self::fragment_shader()
    }
}
type FoliageMaterial = ExtendedMaterial<StandardMaterial, FoliageExtension>;
#[derive(Resource, Default)]
struct Materials(HashMap<(AssetId<StandardMaterial>, u32, Kind, u32), Handle<FoliageMaterial>>);
fn convert(
    mut commands: Commands,
    source: Res<Assets<StandardMaterial>>,
    mut assets: ResMut<Assets<FoliageMaterial>>,
    mut cache: ResMut<Materials>,
    query: Query<(Entity, &MeshMaterial3d<StandardMaterial>, &FoliageSurface)>,
) {
    for (entity, material, surface) in &query {
        let Some(base) = source.get(&material.0) else {
            continue;
        };
        let handle = cache
            .0
            .entry((
                material.0.id(),
                surface.range.to_bits(),
                surface.kind,
                surface.tint.unwrap_or(0.).to_bits(),
            ))
            .or_insert_with(|| {
                let mut base = base.clone();
                if let Some(tint) = surface.tint {
                    // Source foliage_up uses texture OR solid tint, never their product.
                    base.base_color = if base.base_color_texture.is_some() {
                        Color::WHITE
                    } else {
                        Color::srgb(tint, tint, tint)
                    };
                }
                // wind_sway keeps source default specular; ground foliage disables it.
                if surface.kind != Kind::Canopy {
                    base.reflectance = 0.;
                }
                base.perceptual_roughness = 1.;
                base.double_sided = true;
                base.cull_mode = None;
                let end = if surface.range <= 0. {
                    0.
                } else {
                    std::env::var("UG_FOLIAGEFADE")
                        .ok()
                        .and_then(|s| s.parse::<f32>().ok())
                        .filter(|v| v.is_finite() && *v >= 0.)
                        .unwrap_or(surface.range)
                };
                assets.add(ExtendedMaterial {
                    base,
                    extension: FoliageExtension {
                        fade: Vec4::new(
                            end * 0.75,
                            end,
                            if surface.kind == Kind::Canopy { 0. } else { 1. },
                            0.,
                        ),
                        motion: MotionUniform::new(surface.kind),
                    },
                })
            })
            .clone();
        if surface.kind != Kind::Canopy {
            commands.entity(entity).insert(bevy::light::NotShadowCaster);
        }
        commands
            .entity(entity)
            .remove::<MeshMaterial3d<StandardMaterial>>()
            .insert(MeshMaterial3d(handle));
    }
}
fn update(
    rain: Res<RainState>,
    origin: Res<WorldOrigin>,
    field: Res<disturbance::Field>,
    mut materials: ResMut<Assets<FoliageMaterial>>,
    settings: Option<Res<crate::settings::Settings>>,
) {
    let offset = origin.shader_offset();
    // GraphicsOptions.Wind (WindField.SampleWind returns 0) and
    // GrassDisplacement (DisturbanceField packs no slots); both client-only.
    let (wind, displacement) = settings.map_or((true, true), |s| {
        (s.graphics.wind, s.graphics.grass_displacement)
    });
    for (_, material) in materials.iter_mut() {
        let motion = &mut material.extension.motion;
        motion.wind = if wind { rain.wind } else { Vec4::ZERO };
        motion.origin = Vec4::new(
            offset.x,
            offset.y,
            if field.enabled && displacement {
                field.slots.len() as f32
            } else {
                0.
            },
            0.,
        );
        for (i, slot) in field.slots.iter().enumerate() {
            motion.positions[i] = slot.position;
            motion.responses[i] = slot.response;
        }
    }
}

#[cfg(test)]
mod visual;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn material_conversion_keeps_source_texture_or_solid_color_and_leaf_normals() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<StandardMaterial>()
            .init_asset::<FoliageMaterial>()
            .init_asset::<Image>()
            .init_resource::<Materials>()
            .add_systems(Update, convert);
        let image = app
            .world_mut()
            .resource_mut::<Assets<Image>>()
            .add(Image::default());
        let base = app
            .world_mut()
            .resource_mut::<Assets<StandardMaterial>>()
            .add(StandardMaterial {
                base_color: Color::srgb(0.456, 0.456, 0.456),
                base_color_texture: Some(image),
                reflectance: 0.5,
                ..default()
            });
        let flower = app
            .world_mut()
            .spawn((
                MeshMaterial3d(base.clone()),
                FoliageSurface::ground("foliage/flowers_00", 160.),
            ))
            .id();
        let canopy = app
            .world_mut()
            .spawn((MeshMaterial3d(base.clone()), FoliageSurface::canopy()))
            .id();
        let solid = app
            .world_mut()
            .resource_mut::<Assets<StandardMaterial>>()
            .add(StandardMaterial::default());
        let pebble = app
            .world_mut()
            .spawn((
                MeshMaterial3d(solid.clone()),
                FoliageSurface::ground("foliage/pebble_00", 100.),
            ))
            .id();
        let sand = app
            .world_mut()
            .spawn((
                MeshMaterial3d(solid),
                FoliageSurface::ground("foliage/pebble_sand_00", 100.),
            ))
            .id();
        app.update();
        let world = app.world();
        let materials = world.resource::<Assets<FoliageMaterial>>();
        let material = |entity| {
            materials
                .get(
                    &world
                        .get::<MeshMaterial3d<FoliageMaterial>>(entity)
                        .unwrap()
                        .0,
                )
                .unwrap()
        };
        assert_eq!(
            material(flower).base.base_color,
            Color::WHITE,
            "source texture replaces the grey fallback"
        );
        assert_eq!(
            material(pebble).base.base_color,
            Color::srgb(0.456, 0.456, 0.456)
        );
        assert_eq!(
            material(sand).base.base_color,
            Color::srgb(0.506, 0.506, 0.506)
        );
        assert_eq!(material(flower).base.reflectance, 0.);
        assert_eq!(material(canopy).base.reflectance, 0.5);
        assert_eq!(
            material(canopy).extension.fade.z,
            0.,
            "canopy uses authored normals"
        );
        assert!(world.get::<bevy::light::NotShadowCaster>(flower).is_some());
        assert!(world.get::<bevy::light::NotShadowCaster>(canopy).is_none());
        assert_eq!(
            world
                .resource::<Assets<StandardMaterial>>()
                .get(&base)
                .unwrap()
                .base_color,
            Color::srgb(0.456, 0.456, 0.456),
            "shared source material stays untouched"
        );
    }
}
