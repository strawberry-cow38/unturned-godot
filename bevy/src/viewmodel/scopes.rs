//! Source objective-mounted PiP cameras and rigid ocular glass.
use super::*;
use bevy::{
    asset::{RenderAssetUsages, embedded_asset},
    camera::{Hdr, RenderTarget, visibility::VisibilitySystems},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    image::{ImageLoaderSettings, ImageSampler},
    light::NotShadowCaster,
    pbr::{ExtendedMaterial, MaterialExtension},
    render::render_resource::{AsBindGroup, Extent3d, TextureDimension, TextureFormat},
    shader::ShaderRef,
};

#[derive(Component, Clone)]
pub(super) struct ScopeMount {
    pub calibration: Calibration,
    pub reticle: Option<String>,
}
#[derive(Clone, Copy, Debug)]
pub(super) struct Calibration {
    pub lens: Vec3,
    pub objective: Vec3,
    pub aim: Vec3,
    pub fov: f32,
    pub size: f32,
    pub sides: u32,
    pub color: Color,
    pub small_red: bool,
    pub integral: bool,
}
pub(super) fn calibration(mesh: &str) -> Option<Calibration> {
    let (lens, objective, aim, fov, size, sides, color) = match mesh {
        "scope_8x_sight.txt" | "scope_16x_sight.txt" => (
            (0., -0.364, -0.1086),
            (0., 0.149, -0.1086),
            (0., -0.5122, -0.1086),
            if mesh.starts_with("scope_16") {
                5.63
            } else {
                11.25
            },
            0.124,
            12,
            (0.118, 0.118, 0.118),
        ),
        "scope_7x_sight.txt" => (
            (0., -0.364, -0.0868),
            (0., 0.149, -0.0868),
            (0., -0.4893, -0.0868),
            12.86,
            0.115,
            12,
            (0.588, 0.588, 0.588),
        ),
        "makeshift_scope_sight.txt" => (
            (-0.0021, -0.374, -0.1149),
            (-0.0021, 0.120, -0.1149),
            (-0.0021, -0.4705, -0.1149),
            15.,
            0.080,
            6,
            (0.235, 0.235, 0.235),
        ),
        "cross_scope_sight.txt" => (
            (0., -0.347, -0.0724),
            (0., -0.148, -0.0724),
            (0., -0.4667, -0.0724),
            15.,
            0.090,
            12,
            (0.118, 0.118, 0.118),
        ),
        "chevron_scope_sight.txt" => (
            (0., -0.355, -0.076),
            (0., -0.110, -0.076),
            (0., -0.4541, -0.076),
            22.5,
            0.076,
            12,
            (0.118, 0.118, 0.118),
        ),
        "shadowstalker_scope_sight.txt" => (
            (0., -0.364, -0.0927),
            (0., 0.149, -0.0927),
            (0., -0.4893, -0.0927),
            15.,
            0.124,
            4,
            (0.192, 0.18, 0.2),
        ),
        "augewehr_sight.txt" => (
            (0., -0.17, -0.0695),
            (0., 0.1932, -0.0695),
            (0., 0., 0.),
            22.5,
            0.105,
            12,
            (0.3, 0.3, 0.3),
        ),
        _ => return None,
    };
    Some(Calibration {
        lens: lens.into(),
        objective: objective.into(),
        aim: aim.into(),
        fov,
        size,
        sides,
        color: Color::srgb(color.0, color.1, color.2),
        small_red: matches!(mesh, "cross_scope_sight.txt" | "chevron_scope_sight.txt"),
        integral: mesh == "augewehr_sight.txt",
    })
}
#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
struct Glass {
    #[uniform(100)]
    mask: Vec4,
    #[texture(101)]
    #[sampler(102)]
    scene: Handle<Image>,
    #[texture(103)]
    #[sampler(104)]
    reticle: Handle<Image>,
}
impl MaterialExtension for Glass {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/viewmodel/scopes/glass.wesl".into()
    }
}
type GlassMaterial = ExtendedMaterial<StandardMaterial, Glass>;
#[derive(Component)]
pub(crate) struct ScopeView {
    mount: Entity,
    pub(crate) integral: bool,
}
#[derive(Component)]
struct Built;
pub(super) struct ScopePlugin;
impl Plugin for ScopePlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "scopes/glass.wesl");
        app.add_plugins(MaterialPlugin::<GlassMaterial>::default())
            .add_systems(Update, build.after(PlayerUpdate))
            .add_systems(
                PostUpdate,
                position
                    .after(bevy::transform::TransformSystems::Propagate)
                    .before(bevy::light::SimulationLightSystems::UpdateDirectionalLightCascades)
                    .before(VisibilitySystems::UpdateFrusta),
            );
    }
}
fn basis() -> Quat {
    Quat::from_mat3(&Mat3::from_cols(Vec3::NEG_X, Vec3::NEG_Z, Vec3::NEG_Y))
}
fn glass_mask(c: Calibration) -> Vec4 {
    Vec4::new(
        std::f32::consts::TAU / c.sides as f32,
        if c.sides == 4 {
            0.
        } else {
            std::f32::consts::PI / c.sides as f32
        },
        if c.small_red { 0.056 } else { 1. },
        if c.integral {
            2.
        } else if c.small_red {
            1.
        } else {
            0.
        },
    )
}
fn build(
    mut commands: Commands,
    mounts: Query<(Entity, &ScopeMount), Without<Built>>,
    mut images: ResMut<Assets<Image>>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<GlassMaterial>>,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    settings: Option<Res<crate::settings::Settings>>,
) {
    // GraphicsOptions.ScopeSize, read when the optic is next mounted.
    let size = settings.map_or(720, |s| s.graphics.scope_quality.scope_size());
    for (mount, scope) in &mounts {
        let c = scope.calibration;
        // Preserve linear HDR radiance until the viewmodel's single tonemap.
        let mut target = Image::new_target_texture(size, size, TextureFormat::Rgba16Float, None);
        target.sampler = ImageSampler::linear();
        let scene = images.add(target);
        let reticle = scope
            .reticle
            .as_ref()
            .filter(|p| config.content_root.join(p).is_file())
            .map(|p| {
                assets
                    .load_builder()
                    .with_settings(|settings: &mut ImageLoaderSettings| {
                        settings.sampler = ImageSampler::linear();
                    })
                    .load(p.clone())
            })
            .unwrap_or_else(|| {
                images.add(Image::new_fill(
                    Extent3d {
                        width: 1,
                        height: 1,
                        depth_or_array_layers: 1,
                    },
                    TextureDimension::D2,
                    &[0, 0, 0, 0],
                    TextureFormat::Rgba8UnormSrgb,
                    RenderAssetUsages::default(),
                ))
            });
        let material = materials.add(ExtendedMaterial {
            base: StandardMaterial {
                unlit: true,
                cull_mode: None,
                fog_enabled: false,
                ..default()
            },
            extension: Glass {
                mask: glass_mask(c),
                scene: scene.clone(),
                reticle,
            },
        });
        commands.spawn((
            Name::new("Scope rigid ocular glass"),
            Mesh3d(meshes.add(Rectangle::new(c.size, c.size))),
            MeshMaterial3d(material),
            Transform::from_translation(c.lens).with_rotation(basis()),
            RenderLayers::layer(LAYER),
            NotShadowCaster,
            ChildOf(mount),
        ));
        commands.spawn((
            Name::new("Scope objective camera"),
            Camera3d::default(),
            Hdr,
            Camera {
                order: -1,
                is_active: false,
                ..default()
            },
            RenderTarget::Image(scene.into()),
            Projection::Perspective(PerspectiveProjection {
                fov: c.fov.to_radians(),
                aspect_ratio: 1.,
                near: 0.05,
                far: 4000.,
                ..default()
            }),
            Tonemapping::None,
            DebandDither::Disabled,
            Transform::IDENTITY,
            ScopeView {
                mount,
                integral: c.integral,
            },
            RenderLayers::layer(0),
        ));
        commands.entity(mount).insert(Built);
    }
}
fn mapped_objective(
    main: &GlobalTransform,
    view: &GlobalTransform,
    mount: &GlobalTransform,
    c: Calibration,
) -> Transform {
    Transform::from_matrix(
        main.to_matrix()
            * view.to_matrix().inverse()
            * mount.to_matrix()
            * Mat4::from_rotation_translation(basis(), c.objective),
    )
}
fn position(
    mut commands: Commands,
    mounts: Query<(&ScopeMount, &GlobalTransform), Without<ScopeView>>,
    main: Query<(&GlobalTransform, Option<&DistanceFog>), (With<PlayerCamera>, Without<ScopeView>)>,
    vm: Query<(&Camera, &GlobalTransform), (With<ViewmodelCamera>, Without<ScopeView>)>,
    mut views: Query<(
        Entity,
        &ScopeView,
        &mut Transform,
        &mut GlobalTransform,
        &mut Camera,
    )>,
) {
    for (entity, scope, mut local, mut global, mut camera) in &mut views {
        let Ok((mount, pose)) = mounts.get(scope.mount) else {
            commands.entity(entity).despawn();
            continue;
        };
        let (Ok((main, fog)), Ok((view_camera, view))) = (main.single(), vm.single()) else {
            camera.is_active = false;
            continue;
        };
        camera.is_active = view_camera.is_active;
        *local = mapped_objective(main, view, pose, mount.calibration);
        *global = GlobalTransform::from(*local);
        if let Some(fog) = fog {
            commands.entity(entity).insert(fog.clone());
        } else {
            commands.entity(entity).remove::<DistanceFog>();
        }
    }
}
/// The original ADS sensitivity uses actual mounted zoom and blends continuously.
pub(super) fn sensitivity(aim: f32, zoom: f32) -> f32 {
    1. + ((if zoom > 1. { 1. / zoom } else { 0.65 }) - 1.) * aim
}
#[cfg(test)]
mod tests;
