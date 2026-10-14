//! Original grass_displace/foliage_up fragment lighting and distance erosion.
use crate::world::WorldReady;
use bevy::{
    asset::embedded_asset,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::AsBindGroup,
    shader::ShaderRef,
};
use std::collections::HashMap;
pub struct FoliagePlugin;
impl Plugin for FoliagePlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "foliage.wesl");
        app.add_plugins(MaterialPlugin::<FoliageMaterial>::default())
            .init_resource::<Materials>()
            .add_systems(Update, convert.after(WorldReady));
    }
}
#[derive(Component)]
pub struct FoliageSurface(pub f32);
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
pub struct FoliageExtension {
    #[uniform(100)]
    pub fade: Vec4,
}
impl MaterialExtension for FoliageExtension {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/foliage.wesl".into()
    }
    fn deferred_fragment_shader() -> ShaderRef {
        Self::fragment_shader()
    }
}
type FoliageMaterial = ExtendedMaterial<StandardMaterial, FoliageExtension>;
#[derive(Resource, Default)]
struct Materials(HashMap<(AssetId<StandardMaterial>, u32), Handle<FoliageMaterial>>);
fn convert(
    mut commands: Commands,
    source: Res<Assets<StandardMaterial>>,
    mut assets: ResMut<Assets<FoliageMaterial>>,
    mut cache: ResMut<Materials>,
    query: Query<(Entity, &MeshMaterial3d<StandardMaterial>, &FoliageSurface)>,
) {
    for (entity, material, range) in &query {
        let Some(base) = source.get(&material.0) else {
            continue;
        };
        let handle = cache
            .0
            .entry((material.0.id(), range.0.to_bits()))
            .or_insert_with(|| {
                let mut base = base.clone();
                base.reflectance = 0.;
                base.perceptual_roughness = 1.;
                base.double_sided = true;
                base.cull_mode = None;
                let end = std::env::var("UG_FOLIAGEFADE")
                    .ok()
                    .and_then(|s| s.parse::<f32>().ok())
                    .filter(|v| v.is_finite() && *v >= 0.)
                    .unwrap_or(range.0);
                assets.add(ExtendedMaterial {
                    base,
                    extension: FoliageExtension {
                        fade: Vec4::new(end * 0.75, end, 0., 0.),
                    },
                })
            })
            .clone();
        commands
            .entity(entity)
            .remove::<MeshMaterial3d<StandardMaterial>>()
            .insert(MeshMaterial3d(handle));
    }
}
