//! Source water.gdshader / WaterReflection.cs: a flat ocean, ripple normals,
//! depth-derived shore foam, and a half-resolution live planar mirror.
use super::{WorldObject, WorldReady, WorldState, WorldTerrain, origin::WorldOrigin};
use crate::player::{PlayerCamera, PlayerCameraUpdate};
use bevy::{
    asset::embedded_asset,
    camera::{RenderTarget, visibility::RenderLayers},
    core_pipeline::prepass::DepthPrepass,
    image::ImageSampler,
    light::NotShadowCaster,
    pbr::{ExtendedMaterial, MaterialExtension},
    prelude::*,
    render::render_resource::{AsBindGroup, TextureFormat},
    shader::ShaderRef,
    window::PrimaryWindow,
};

const WATER_LAYER: usize = 17;
const REFLECTION_LAYER: usize = 18;

pub(super) fn ocean_bounds(
    patches: &[super::TerrainPatch],
    procedural: bool,
    focus: Vec3,
) -> (Vec2, Vec2) {
    if procedural {
        // RegionStreamer.FollowWater / AddWater, in whole 256 m regions.
        return (
            (focus.xz() / 256.).floor() * 256. + Vec2::splat(128.),
            Vec2::splat(23. * 256.),
        );
    }
    let min = patches
        .iter()
        .fold(Vec2::splat(f32::INFINITY), |v, p| v.min(p.origin));
    let max = patches.iter().fold(Vec2::splat(f32::NEG_INFINITY), |v, p| {
        v.max(p.origin + Vec2::splat(p.cells as f32 * p.spacing))
    });
    // Terrain.Load uses GW*UNIT, including the shared last height vertex:
    // that is +2 m in X and -2 m in reflected Z from the tile-bounds centre.
    (
        (min + max) * 0.5 + Vec2::new(2., -2.),
        max - min + Vec2::splat(400.),
    )
}

#[derive(Component)]
pub struct WaterSurface;
/// The original MarkReflective call sites: terrain, props, trees and resources.
/// Ground cover and particles deliberately remain outside this layer.
#[derive(Component)]
pub struct Reflective;
#[derive(Component)]
pub(crate) struct ReflectionCamera;
#[derive(Component)]
struct ReflectionLight(Entity);
#[derive(Component)]
struct ReflectionMarked;

#[derive(Resource)]
pub struct WaterSettings {
    pub enabled: bool,
    pub every_frames: u32,
    pub strength: f32,
    pub debug: bool,
}
impl Default for WaterSettings {
    fn default() -> Self {
        Self {
            enabled: std::env::var("UG_REFLECT").as_deref() != Ok("0"),
            every_frames: std::env::var("UG_REFLEVERY")
                .ok()
                .and_then(|s| s.parse().ok())
                .unwrap_or(1)
                .max(1),
            strength: std::env::var("UG_REFLSTR")
                .ok()
                .and_then(|s| s.parse::<f32>().ok())
                .filter(|v| v.is_finite())
                .unwrap_or(1.),
            debug: std::env::var("UG_REFLDEBUG").as_deref() == Ok("1"),
        }
    }
}
#[derive(Resource, Default)]
struct Mirror {
    texture: Handle<Image>,
    size: UVec2,
    frame: u32,
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
struct WaterExtension {
    /// time, modulo source origin X/Z, live mirror enabled
    #[uniform(100)]
    motion: Vec4,
    /// reflection strength, debug view, shore foam enabled, unused
    #[uniform(101)]
    settings: Vec4,
    #[texture(102)]
    #[sampler(103)]
    reflection: Handle<Image>,
}
impl MaterialExtension for WaterExtension {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/world/water.wesl".into()
    }
}
type WaterMaterial = ExtendedMaterial<StandardMaterial, WaterExtension>;
pub struct WaterPlugin;
impl Plugin for WaterPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "water.wesl");
        install_lights(app);
        app.add_plugins(MaterialPlugin::<WaterMaterial>::default())
            .init_resource::<WaterSettings>()
            .init_resource::<Mirror>()
            .add_systems(Update, (prepare, mark_reflective).chain().after(WorldReady))
            .add_systems(
                Update,
                update_mirror.after(PlayerCameraUpdate).after(prepare),
            );
    }
}
fn install_lights(app: &mut App) {
    app.add_systems(
        PostUpdate,
        sync_lights
            .after(bevy::transform::TransformSystems::Propagate)
            .after(crate::viewmodel::lights::LightPresentation)
            .before(bevy::camera::visibility::VisibilitySystems::VisibilityPropagate)
            .before(bevy::light::SimulationLightSystems::UpdateBounds)
            .before(bevy::light::SimulationLightSystems::UpdateDirectionalLightCascades),
    );
}
fn mirror_size(window: &Window) -> UVec2 {
    // Source SizeToWindow: preserve aspect from the very first sample.
    UVec2::new(
        (window.physical_width() / 2).max(64),
        (window.physical_height() / 2).max(64),
    )
}
fn target(size: UVec2) -> Image {
    // Hardware sRGB sampling undoes the target write conversion. Applying the
    // Godot workaround's explicit srgb_to_linear here would decode twice.
    let mut image = Image::new_target_texture(
        size.x,
        size.y,
        TextureFormat::Rgba8Unorm,
        Some(TextureFormat::Rgba8UnormSrgb),
    );
    image.sampler = ImageSampler::linear();
    image
}
fn prepare(
    mut commands: Commands,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut mirror: ResMut<Mirror>,
    mut images: ResMut<Assets<Image>>,
    mut materials: ResMut<Assets<WaterMaterial>>,
    surfaces: Query<Entity, (With<WaterSurface>, Without<MeshMaterial3d<WaterMaterial>>)>,
    cameras: Query<(Entity, Option<&RenderLayers>, Option<&DepthPrepass>), With<PlayerCamera>>,
) {
    let Ok(window) = windows.single() else { return };
    if mirror.texture == Handle::default() {
        mirror.size = mirror_size(window);
        mirror.texture = images.add(target(mirror.size));
        commands.spawn((
            Name::new("Source planar water reflection"),
            ReflectionCamera,
            Camera3d::default(),
            Camera {
                order: -1,
                is_active: false,
                ..default()
            },
            RenderTarget::Image(mirror.texture.clone().into()),
            RenderLayers::layer(REFLECTION_LAYER),
            Msaa::Off,
            Transform::default(),
        ));
    }
    for (entity, layers, depth) in &cameras {
        if depth.is_none()
            || !layers.is_some_and(|layers| layers.intersects(&RenderLayers::layer(WATER_LAYER)))
        {
            commands.entity(entity).insert((
                DepthPrepass,
                layers.cloned().unwrap_or_default().with(WATER_LAYER),
            ));
        }
    }
    for entity in &surfaces {
        let material = materials.add(WaterMaterial {
            base: StandardMaterial {
                alpha_mode: AlphaMode::Blend,
                perceptual_roughness: 1.,
                reflectance: 0.,
                double_sided: true,
                cull_mode: None,
                ..default()
            },
            extension: WaterExtension {
                motion: Vec4::ZERO,
                settings: Vec4::new(1., 0., 1., 0.),
                reflection: mirror.texture.clone(),
            },
        });
        commands
            .entity(entity)
            .remove::<MeshMaterial3d<StandardMaterial>>()
            .insert((
                MeshMaterial3d(material),
                RenderLayers::layer(WATER_LAYER),
                NotShadowCaster,
            ));
    }
}
fn mark_reflective(
    mut commands: Commands,
    meshes: Query<
        (Entity, Option<&RenderLayers>),
        (
            With<Mesh3d>,
            Without<WaterSurface>,
            Without<ReflectionMarked>,
            Or<(
                With<WorldTerrain>,
                With<WorldObject>,
                With<Reflective>,
                With<crate::environment::Sky>,
            )>,
        ),
    >,
) {
    for (entity, layers) in &meshes {
        commands.entity(entity).insert((
            layers.cloned().unwrap_or_default().with(REFLECTION_LAYER),
            ReflectionMarked,
        ));
    }
}
fn scene_light(layers: Option<&RenderLayers>) -> bool {
    layers.is_none_or(|layers| layers.intersects(&RenderLayers::layer(0)))
}
fn sync_lights(
    mut commands: Commands,
    source: Query<
        (
            Entity,
            &GlobalTransform,
            Option<&DirectionalLight>,
            Option<&PointLight>,
            Option<&SpotLight>,
            Option<&RenderLayers>,
            Option<&crate::hdr::SourceFalloff>,
        ),
        (
            Without<ReflectionLight>,
            Or<(With<DirectionalLight>, With<PointLight>, With<SpotLight>)>,
        ),
    >,
    copies: Query<(Entity, &ReflectionLight)>,
    visibility: Query<(Option<&Visibility>, Option<&ChildOf>)>,
) {
    // Same visible scene lights, with shadow maps disabled only for this pass.
    // The viewmodel fill belongs to layer1 and must never brighten the ocean.
    for (entity, id) in &copies {
        if source
            .get(id.0)
            .map_or(true, |(_, _, _, _, _, layers, _)| !scene_light(layers))
        {
            commands.entity(entity).despawn();
        }
    }
    for (source_entity, pose, directional, point, spot, layers, falloff) in &source {
        if !scene_light(layers) {
            continue;
        }
        let entity = copies
            .iter()
            .find(|(_, id)| id.0 == source_entity)
            .map(|(entity, _)| entity)
            .unwrap_or_else(|| {
                commands
                    .spawn((
                        Name::new("Source water reflection light"),
                        ReflectionLight(source_entity),
                        RenderLayers::layer(REFLECTION_LAYER),
                    ))
                    .id()
            });
        let mut copy = commands.entity(entity);
        if let Some(profile) = falloff {
            copy.insert(*profile);
        } else {
            copy.remove::<crate::hdr::SourceFalloff>();
        }
        // This runs after propagation, so publish both forms before extraction.
        copy.insert((
            pose.compute_transform(),
            *pose,
            if crate::viewmodel::lighting::visible_in_tree(source_entity, &visibility) {
                Visibility::Visible
            } else {
                Visibility::Hidden
            },
        ));
        if let Some(light) = directional {
            let mut light = light.clone();
            light.shadow_maps_enabled = false;
            light.contact_shadows_enabled = false;
            copy.insert(light);
        }
        if let Some(light) = point {
            let mut light = light.clone();
            light.shadow_maps_enabled = false;
            light.contact_shadows_enabled = false;
            copy.insert(light);
        }
        if let Some(light) = spot {
            let mut light = light.clone();
            light.shadow_maps_enabled = false;
            light.contact_shadows_enabled = false;
            copy.insert(light);
        }
    }
}
/// A proper rotation avoids reversing triangle winding. Its local Y is flipped
/// relative to the exact negative-determinant reflection, so the shader flips
/// texture Y. The projection test proves this is exact across the entire image.
fn reflected_pose(main: Transform, sea: f32) -> Transform {
    let reflect = |p: Vec3| Vec3::new(p.x, -p.y, p.z);
    let forward = reflect(main.rotation * Vec3::NEG_Z);
    let up = -reflect(main.rotation * Vec3::Y);
    Transform::from_translation(Vec3::new(
        main.translation.x,
        2. * sea - main.translation.y,
        main.translation.z,
    ))
    .looking_to(forward, up)
}
fn update_mirror(
    time: Res<Time>,
    settings: Res<WaterSettings>,
    origin: Res<WorldOrigin>,
    world: Res<WorldState>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut mirror: ResMut<Mirror>,
    mut images: ResMut<Assets<Image>>,
    main: Query<
        (&Transform, &Projection, Option<&DistanceFog>),
        (With<PlayerCamera>, Without<ReflectionCamera>),
    >,
    mut cameras: Query<
        (Entity, &mut Transform, &mut Projection, &mut Camera),
        With<ReflectionCamera>,
    >,
    mut commands: Commands,
    mut materials: ResMut<Assets<WaterMaterial>>,
) {
    let Ok((main, projection, fog)) = main.single() else {
        return;
    };
    let enabled = settings.enabled && world.sea_level.is_some();
    if let Ok(window) = windows.single() {
        let size = mirror_size(window);
        if size != mirror.size {
            if let Some(mut image) = images.get_mut(&mirror.texture) {
                *image = target(size);
            }
            mirror.size = size;
        }
    }
    mirror.frame = mirror.frame.wrapping_add(1);
    for (entity, mut pose, mut lens, mut camera) in &mut cameras {
        *pose = reflected_pose(*main, world.sea_level.unwrap_or(0.));
        *lens = projection.clone();
        camera.is_active = enabled && mirror.frame % settings.every_frames.max(1) == 0;
        if let Some(fog) = fog {
            commands.entity(entity).insert(fog.clone());
        } else {
            commands.entity(entity).remove::<DistanceFog>();
        }
    }
    let offset = origin.shader_offset();
    for (_, material) in materials.iter_mut() {
        material.extension.motion = Vec4::new(
            time.elapsed_secs(),
            offset.x,
            offset.y,
            if enabled { 1. } else { 0. },
        );
        material.extension.settings = Vec4::new(
            settings.strength,
            if settings.debug { 1. } else { 0. },
            1.,
            0.,
        );
    }
}

#[cfg(test)]
mod light_tests;
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mirror_projection_tracks_main_reflected_points_at_edges_and_rolls() {
        let sea = 25.6;
        let main = Transform::from_xyz(-450., 33., -100.).with_rotation(Quat::from_euler(
            EulerRot::YXZ,
            0.7,
            -0.23,
            0.12,
        ));
        let mirror = reflected_pose(main, sea);
        let projection = Mat4::perspective_infinite_reverse_rh(70_f32.to_radians(), 16. / 9., 0.1);
        let clip = |camera: Transform, point: Vec3| {
            let p = projection * camera.to_matrix().inverse() * point.extend(1.);
            p.truncate() / p.w
        };
        for local in [
            Vec3::new(-14., 3., -35.),
            Vec3::new(12., -5., -26.),
            Vec3::new(0., 0., -20.),
        ] {
            let object = main.transform_point(local);
            let reflected = Vec3::new(object.x, 2. * sea - object.y, object.z);
            let a = clip(main, reflected);
            let b = clip(mirror, object);
            assert!((a.x - b.x).abs() < 0.00002);
            assert!((a.y + b.y).abs() < 0.00002);
            assert!((a.z - b.z).abs() < 0.00002);
        }
        assert!((mirror.scale - Vec3::ONE).length() < 0.00001);
        assert!((Mat3::from_quat(mirror.rotation).determinant() - 1.).abs() < 0.00001);
    }
    #[test]
    fn reflection_target_carries_initial_window_aspect() {
        let window = Window {
            resolution: (1280, 720).into(),
            ..default()
        };
        assert_eq!(mirror_size(&window), UVec2::new(640, 360));
        assert_eq!(
            target(mirror_size(&window)).texture_descriptor.size.width,
            640
        );
    }
    #[test]
    fn reflected_lighting_excludes_first_person_fill() {
        assert!(scene_light(None));
        assert!(scene_light(Some(&RenderLayers::layer(0))));
        assert!(!scene_light(Some(&RenderLayers::layer(1))));
        assert!(!scene_light(Some(&RenderLayers::layer(18))));
    }
    #[test]
    fn underwater_depth_prepass_does_not_hide_the_water_surface() {
        let mut app = App::new();
        app.add_plugins(MinimalPlugins)
            .init_resource::<Mirror>()
            .init_resource::<Assets<Image>>()
            .init_resource::<Assets<WaterMaterial>>()
            .add_systems(Update, prepare);
        app.world_mut().spawn((Window::default(), PrimaryWindow));
        let camera = app.world_mut().spawn((PlayerCamera, DepthPrepass)).id();
        app.update();
        let layers = app.world().get::<RenderLayers>(camera).unwrap();
        assert!(layers.intersects(&RenderLayers::layer(WATER_LAYER)));
        assert!(layers.intersects(&RenderLayers::layer(0)));
    }
    #[test]
    fn ocean_extent_keeps_source_retail_margin_and_generated_region_steps() {
        let (center, size) = ocean_bounds(&[], true, Vec3::new(-0.1, 0., 511.9));
        assert_eq!(center, Vec2::new(-128., 384.));
        assert_eq!(size, Vec2::splat(5888.));
        let patch = super::super::decode_retail_heightmap(&vec![0; 257 * 257 * 2], -1, 0).unwrap();
        let (center, size) = ocean_bounds(&[patch], false, Vec3::ZERO);
        assert_eq!(center, Vec2::new(-510., -514.));
        assert_eq!(size, Vec2::splat(1424.));
    }
}
