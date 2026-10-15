//! WetSurface.Wrap retains the original base for other source material consumers.
use super::{RainState, RainUpdate, roof::RoofMap};
use crate::world::{RoadSurface, WorldObject, WorldTerrain, origin::WorldOrigin};
use bevy::{
    asset::embedded_asset,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::{AsBindGroup, ShaderType},
    shader::ShaderRef,
};
use std::collections::HashMap;

#[derive(Component, Clone)]
pub struct WetBase(pub Handle<StandardMaterial>);
#[derive(Component)]
pub struct WetTarget(pub bool);
#[derive(Debug, Clone, Reflect, ShaderType, Default)]
pub struct RainUniform {
    pub roof_rect: Vec4,
    /// Wetness, intensity, slow puddle level, elapsed render time.
    pub weather: Vec4,
    /// Source splash lean XY and absolute shader origin XZ.
    pub slant_origin: Vec4,
}
impl RainUniform {
    pub fn new(rain: &RainState, roof: &RoofMap, origin: &WorldOrigin) -> Self {
        let offset = origin.shader_offset();
        Self {
            roof_rect: roof.rect,
            weather: Vec4::new(rain.wetness, rain.intensity, rain.puddle, rain.elapsed),
            slant_origin: Vec4::new(rain.slant.x, rain.slant.y, offset.x, offset.y),
        }
    }
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
pub struct WetExtension {
    #[uniform(100)]
    rain: RainUniform,
    #[texture(101, filterable = false)]
    roof: Handle<Image>,
    #[uniform(102)]
    surface: Vec4,
}
impl MaterialExtension for WetExtension {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/rain/wet.wesl".into()
    }
    fn deferred_fragment_shader() -> ShaderRef {
        Self::fragment_shader()
    }
}
type WetMaterial = ExtendedMaterial<StandardMaterial, WetExtension>;
#[derive(Resource, Default)]
struct Materials(HashMap<(AssetId<StandardMaterial>, bool), Handle<WetMaterial>>);
pub struct WetPlugin;
impl Plugin for WetPlugin {
    fn build(&self, app: &mut App) {
        bevy::shader::load_shader_library!(app, "surface.wesl");
        embedded_asset!(app, "wet.wesl");
        app.init_resource::<Materials>()
            .add_plugins(MaterialPlugin::<WetMaterial>::default())
            .add_systems(Update, (convert, update).chain().after(RainUpdate));
    }
}
fn eligible(material: &StandardMaterial) -> bool {
    matches!(material.alpha_mode, AlphaMode::Opaque)
        && material.base_color.alpha() >= 0.999
        && material.emissive.red == 0.
        && material.emissive.green == 0.
        && material.emissive.blue == 0.
}
fn puddle_prop(name: &str) -> bool {
    name.starts_with("Road_") || name.starts_with("Block_Road") || name == "Dock_1"
}
fn convert(
    mut commands: Commands,
    source: Res<Assets<StandardMaterial>>,
    mut materials: ResMut<Assets<WetMaterial>>,
    mut cache: ResMut<Materials>,
    rain: Res<RainState>,
    roof: Res<RoofMap>,
    origin: Res<WorldOrigin>,
    query: Query<
        (
            Entity,
            &MeshMaterial3d<StandardMaterial>,
            Option<&WorldObject>,
            Option<&RoadSurface>,
            Option<&Name>,
            Option<&WetTarget>,
        ),
        (
            Without<crate::world::terrain::TerrainSurface>,
            Or<(
                With<WorldObject>,
                With<RoadSurface>,
                With<WorldTerrain>,
                With<WetTarget>,
            )>,
        ),
    >,
) {
    if std::env::var("UG_WETPROPS").as_deref() == Ok("0") {
        return;
    }
    for (entity, material, object, road, name, target) in &query {
        let Some(base) = source.get(&material.0) else {
            continue;
        };
        if !eligible(base) {
            continue;
        }
        let puddles = if let Some(target) = target {
            target.0
        } else if let Some(road) = road {
            if !road.concrete {
                continue;
            }
            true
        } else if let Some(object) = object {
            if object.guid.is_empty() {
                continue;
            }
            puddle_prop(&object.asset_name)
        } else {
            let name = name.map_or("", Name::as_str);
            if !name.starts_with("Generated ")
                || name.starts_with("Generated tunnel")
                || name.ends_with("road_5.png")
                || name.ends_with("layer3.png")
            {
                continue;
            }
            true
        };
        let handle = cache
            .0
            .entry((material.0.id(), puddles))
            .or_insert_with(|| {
                let mut base = base.clone();
                base.metallic = 0.;
                materials.add(ExtendedMaterial {
                    extension: WetExtension {
                        rain: RainUniform::new(&rain, &roof, &origin),
                        roof: roof.texture.clone(),
                        surface: Vec4::new(
                            base.perceptual_roughness,
                            if puddles { 1. } else { 0. },
                            0.,
                            0.,
                        ),
                    },
                    base,
                })
            })
            .clone();
        commands
            .entity(entity)
            .remove::<MeshMaterial3d<StandardMaterial>>()
            .insert((WetBase(material.0.clone()), MeshMaterial3d(handle)));
    }
}
fn update(
    rain: Res<RainState>,
    roof: Res<RoofMap>,
    origin: Res<WorldOrigin>,
    mut materials: ResMut<Assets<WetMaterial>>,
) {
    for (_, material) in materials.iter_mut() {
        material.extension.rain = RainUniform::new(&rain, &roof, &origin);
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_wet_wrapper_excludes_cutouts_and_emission_and_limits_puddles() {
        assert!(eligible(&StandardMaterial::default()));
        assert!(!eligible(&StandardMaterial {
            alpha_mode: AlphaMode::Mask(0.5),
            ..default()
        }));
        assert!(!eligible(&StandardMaterial {
            emissive: LinearRgba::WHITE,
            ..default()
        }));
        assert!(puddle_prop("Dock_1"));
        assert!(puddle_prop("Block_Road_0"));
        assert!(puddle_prop("Road_0"));
        assert!(!puddle_prop("Dock_0"));
        assert!(!puddle_prop("Warehouse_0"));
    }
    #[test]
    fn actual_material_conversion_preserves_source_base_and_separates_puddle_eligibility() {
        let mut app = App::new();
        app.add_plugins((MinimalPlugins, AssetPlugin::default()))
            .init_asset::<StandardMaterial>()
            .init_asset::<WetMaterial>()
            .init_resource::<Materials>()
            .init_resource::<RainState>()
            .init_resource::<RoofMap>()
            .init_resource::<WorldOrigin>()
            .add_systems(Update, convert);
        let base = app
            .world_mut()
            .resource_mut::<Assets<StandardMaterial>>()
            .add(StandardMaterial::default());
        let spawn = |world: &mut World, name: &str| {
            world
                .spawn((
                    WorldObject {
                        guid: "retail-guid".into(),
                        asset_name: name.into(),
                        source_index: 0,
                    },
                    MeshMaterial3d(base.clone()),
                ))
                .id()
        };
        let road = spawn(app.world_mut(), "Road_0");
        let wall = spawn(app.world_mut(), "Warehouse_0");
        app.update();
        for entity in [road, wall] {
            assert!(
                app.world()
                    .get::<MeshMaterial3d<StandardMaterial>>(entity)
                    .is_none()
            );
            assert_eq!(
                app.world().get::<WetBase>(entity).unwrap().0,
                base,
                "doors must retain their original source material after weather conversion"
            );
        }
        let road_material = &app
            .world()
            .get::<MeshMaterial3d<WetMaterial>>(road)
            .unwrap()
            .0;
        let wall_material = &app
            .world()
            .get::<MeshMaterial3d<WetMaterial>>(wall)
            .unwrap()
            .0;
        assert_ne!(road_material, wall_material);
        let assets = app.world().resource::<Assets<WetMaterial>>();
        assert_eq!(assets.get(road_material).unwrap().extension.surface.y, 1.);
        assert_eq!(assets.get(wall_material).unwrap().extension.surface.y, 0.);
    }
}
