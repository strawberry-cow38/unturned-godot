//! Original OutlineOverlay: the focused target's meshes are mirrored white and
//! unlit onto `OUTLINE_LAYER` (MakeOutline). A mask camera slaved to the player
//! camera renders only that layer, and a fullscreen dilate (item_outline.gdshader)
//! paints the rim in WorldItem.FocusColor above the scene, at source canvas
//! layer 50 (above the tool label, tinted by the night-vision tube).
use crate::{
    entities::{ApplyInteractions, InteractionFocus},
    player::PlayerCamera,
    ui::layers::OUTLINE_ORDER,
};
use bevy::{
    asset::embedded_asset,
    camera::{Hdr, RenderTarget},
    core_pipeline::tonemapping::Tonemapping,
    light::NotShadowCaster,
    prelude::*,
    render::render_resource::{AsBindGroup, Extent3d, TextureFormat},
    camera::visibility::RenderLayers,
    image::ImageSampler,
    shader::ShaderRef,
    window::PrimaryWindow,
};

/// OutlineOverlay.OutlineLayer (1 << 19): main cameras never render it.
pub(crate) const OUTLINE_LAYER: usize = 19;
/// `_mat.SetShaderParameter(thickness, 3.5f)`.
const THICKNESS_PX: f32 = 3.5;

#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
pub(crate) struct OutlineMaterial {
    /// Linear rim colour (FocusColor × night-vision tint).
    #[uniform(0)]
    color: Vec4,
    /// Thickness px, submersion, wobble amplitude, TIME × wobble speed.
    #[uniform(1)]
    params: Vec4,
    #[texture(2)]
    #[sampler(3)]
    mask: Handle<Image>,
}
impl UiMaterial for OutlineMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/outline/item_outline.wesl".into()
    }
}

/// The silhouette mirrors the overlay currently draws a rim around.
#[derive(Resource, Default)]
pub(crate) struct Outline {
    pub target: Option<Entity>,
    pub color: Color,
    pub(crate) mirrors: Vec<Entity>,
    mirrored: Option<Entity>,
    pub(crate) mask: Option<Mask>,
    white: Option<Handle<StandardMaterial>>,
}
#[derive(Clone)]
pub(crate) struct Mask {
    pub(crate) image: Handle<Image>,
    pub(crate) camera: Entity,
    pub(crate) ui: Entity,
    pub(crate) overlay: Entity,
    material: Handle<OutlineMaterial>,
}
#[derive(Component)]
pub(crate) struct OutlineMirror;
#[derive(Component)]
struct MaskCamera;
#[derive(Component)]
struct Overlay;

pub(crate) struct OutlinePlugin;
impl Plugin for OutlinePlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "outline/item_outline.wesl");
        app.add_plugins(UiMaterialPlugin::<OutlineMaterial>::default())
            .init_resource::<Outline>()
            .add_systems(
                Update,
                (adopt, mirror, present).chain().after(ApplyInteractions),
            );
    }
}

/// The winning interaction candidate claims the silhouette and its colour
/// (OutlineOverlay.ShowOutline); nothing is outlined from a vehicle seat
/// (DrivingSuppress), because nothing is focusable there.
fn adopt(focus: Res<InteractionFocus>, mut outline: ResMut<Outline>) {
    let claimed = focus.outlined();
    outline.target = claimed.map(|(e, _)| e);
    if let Some((_, color)) = claimed {
        outline.color = color;
    }
}

/// MakeOutline: an unshaded, double-sided white copy of each mesh under the
/// target, on the outline layer only, parented so it follows doors and bodies.
fn mirror(
    mut commands: Commands,
    mut outline: ResMut<Outline>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    meshes: Query<&Mesh3d, (Without<OutlineMirror>, Without<bevy::mesh::skinning::SkinnedMesh>)>,
    children: Query<&Children>,
) {
    if outline.mirrored == outline.target
        && outline.mirrors.iter().all(|m| commands.get_entity(*m).is_ok())
    {
        return;
    }
    for mirror in std::mem::take(&mut outline.mirrors) {
        if let Ok(mut entity) = commands.get_entity(mirror) {
            entity.despawn();
        }
    }
    outline.mirrored = outline.target;
    let Some(target) = outline.target else {
        return;
    };
    let white = outline
        .white
        .get_or_insert_with(|| {
            materials.add(StandardMaterial {
                base_color: Color::WHITE,
                unlit: true,
                double_sided: true,
                cull_mode: None,
                ..default()
            })
        })
        .clone();
    let mut sources = vec![target];
    sources.extend(children.iter_descendants(target));
    for source in sources {
        let Ok(mesh) = meshes.get(source) else { continue };
        outline.mirrors.push(
            commands
                .spawn((
                    Name::new("Outline silhouette"),
                    OutlineMirror,
                    Mesh3d(mesh.0.clone()),
                    MeshMaterial3d(white.clone()),
                    Transform::IDENTITY,
                    RenderLayers::layer(OUTLINE_LAYER),
                    NotShadowCaster,
                    ChildOf(source),
                ))
                .id(),
        );
    }
}

#[allow(clippy::too_many_arguments)]
fn present(
    mut commands: Commands,
    mut outline: ResMut<Outline>,
    mut images: ResMut<Assets<Image>>,
    mut materials: ResMut<Assets<OutlineMaterial>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    player: Query<(Entity, &Projection), (With<PlayerCamera>, Without<MaskCamera>)>,
    mut masks: Query<(&mut Camera, &mut Projection, Option<&ChildOf>), With<MaskCamera>>,
    mut overlays: Query<&mut Visibility, With<Overlay>>,
    mut ui_cameras: Query<&mut Camera, (Without<MaskCamera>, Without<PlayerCamera>)>,
    nightvision: Option<Res<crate::nightvision::NightVision>>,
    underwater: Option<Res<crate::world::underwater::UnderwaterState>>,
    time: Res<Time>,
    settings: Option<Res<crate::settings::Settings>>,
) {
    let (Ok(window), Ok((player, projection))) = (windows.single(), player.single()) else {
        return;
    };
    let size = window.physical_size().max(UVec2::ONE);
    // Recreate after a teardown removed the cameras.
    if outline.mask.as_ref().is_some_and(|m| {
        !masks.contains(m.camera) || !overlays.contains(m.overlay) || !ui_cameras.contains(m.ui)
    }) {
        if let Some(m) = outline.mask.take() {
            for entity in [m.camera, m.ui, m.overlay] {
                if let Ok(mut e) = commands.get_entity(entity) {
                    e.despawn();
                }
            }
        }
    }
    let mask = outline.mask.get_or_insert_with(|| {
        let mut target = Image::new_target_texture(size.x, size.y, TextureFormat::Rgba8UnormSrgb, None);
        target.sampler = ImageSampler::linear();
        let image = images.add(target);
        let camera = commands
            .spawn((
                Name::new("Source outline mask camera"),
                MaskCamera,
                Camera3d::default(),
                Camera {
                    order: -15,
                    clear_color: ClearColorConfig::Custom(Color::NONE),
                    is_active: false,
                    ..default()
                },
                RenderTarget::Image(image.clone().into()),
                Tonemapping::None,
                Msaa::Off,
                projection.clone(),
                RenderLayers::layer(OUTLINE_LAYER),
                Transform::IDENTITY,
                ChildOf(player),
            ))
            .id();
        let material = materials.add(OutlineMaterial { color: Vec4::ONE, params: Vec4::ZERO, mask: image.clone() });
        let ui = commands
            .spawn((
                Name::new("Source outline layer50"),
                Camera2d,
                Camera { order: OUTLINE_ORDER, clear_color: ClearColorConfig::None, is_active: false, ..default() },
                Hdr,
                Tonemapping::None,
            ))
            .id();
        let overlay = commands
            .spawn((
                Name::new("Source outline rim"),
                Overlay,
                Node { position_type: PositionType::Absolute, width: percent(100.), height: percent(100.), ..default() },
                MaterialNode(material.clone()),
                UiTargetCamera(ui),
                Visibility::Hidden,
                bevy::picking::Pickable::IGNORE,
            ))
            .id();
        Mask { image, camera, ui, overlay, material }
    });
    let Mask { image, camera, ui, overlay, material } = mask.clone();
    // GraphicsOptions.Outline (retail OutlineQuality Off) disables the whole pass.
    let on = outline.target.is_some()
        && !outline.mirrors.is_empty()
        && settings.as_ref().is_none_or(|s| s.graphics.outline);
    if let Ok((mut mask, mut mask_projection, parent)) = masks.get_mut(camera) {
        if mask.is_active != on {
            mask.is_active = on;
        }
        // Match the active camera exactly so the mask aligns pixel-for-pixel.
        *mask_projection = projection.clone();
        if parent.map(|p| p.parent()) != Some(player) {
            commands.entity(camera).insert(ChildOf(player));
        }
    }
    if let Ok(mut visibility) = overlays.get_mut(overlay) {
        let wanted = if on { Visibility::Inherited } else { Visibility::Hidden };
        if *visibility != wanted {
            *visibility = wanted;
        }
    }
    // The rim pass only runs while something is focused (no stale mask on screen).
    if let Ok(mut ui) = ui_cameras.get_mut(ui)
        && ui.is_active != on
    {
        ui.is_active = on;
    }
    if let Some(mut target) = images.get_mut(&image)
        && (target.width(), target.height()) != (size.x, size.y)
    {
        target.resize(Extent3d { width: size.x, height: size.y, depth_or_array_layers: 1 });
    }
    // Modulate = NightVision.OverlayTint, multiplied in the source's sRGB space.
    let tint = nightvision.map_or(Color::WHITE, |nv| nv.overlay_tint()).to_srgba();
    let rim = outline.color.to_srgba();
    let color = Color::srgb(rim.red * tint.red, rim.green * tint.green, rim.blue * tint.blue).to_linear();
    let submersion = underwater.is_some_and(|u| u.active) as u8 as f32;
    if let Some(mut m) = materials.get_mut(&material) {
        m.color = Vec4::new(color.red, color.green, color.blue, 1.);
        m.params = Vec4::new(
            THICKNESS_PX,
            submersion,
            crate::world::underwater::WOBBLE,
            time.elapsed_secs() * crate::world::underwater::WOBBLE_SPEED,
        );
    }
}

#[cfg(test)]
mod tests;
