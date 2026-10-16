//! WireHudSet's layer40,26px font,600px minimum label and1.5px source outline.
use super::*;
use crate::character::nameplate::raster::{OriginalFont, Raster};
use bevy::{
    camera::Hdr, core_pipeline::tonemapping::Tonemapping, image::ImageSampler, text::TextPipeline,
};

/// Other layer-40 tool readouts sharing the slot (source RopeHudSet uses an
/// identical label; the held tools are mutually exclusive).
#[derive(Resource, Default)]
pub(crate) struct ToolReadouts {
    pub rope: String,
}
/// The layer-40 label draw; readout writers order before it.
#[derive(SystemSet, Debug, Clone, PartialEq, Eq, Hash)]
pub(crate) struct ToolHud;
#[derive(Resource, Default)]
struct Hud {
    text: String,
    entity: Option<Entity>,
    camera: Option<Entity>,
    image: Option<Handle<Image>>,
}
pub(super) fn install(app: &mut App) {
    if !app.world().contains_resource::<Assets<Font>>() {
        app.init_asset::<Font>();
    }
    app.init_resource::<OriginalFont>()
        .init_resource::<Hud>()
        .init_resource::<ToolReadouts>()
        .init_resource::<Preview>()
        .add_systems(
            Update,
            draw.after(super::input)
                .in_set(ToolHud)
                .run_if(resource_exists::<TextPipeline>),
        );
}
fn draw(
    mut commands: Commands,
    gesture: Res<Gesture>,
    readouts: Res<ToolReadouts>,
    mut hud: ResMut<Hud>,
    mut raster: Raster,
    mut cameras: Query<&mut Camera>,
    labels: Query<(), With<ImageNode>>,
) {
    // Recreate rather than address entities a world teardown already removed.
    if hud.camera.is_some_and(|c| !cameras.contains(c)) {
        hud.camera = None;
        if let Some(entity) = hud.entity.take()
            && labels.contains(entity)
        {
            commands.entity(entity).despawn();
        }
        hud.text.clear();
    }
    if hud.entity.is_some_and(|e| !labels.contains(e)) {
        hud.entity = None;
        hud.text.clear();
    }
    let text = if gesture.hud.is_empty() { &readouts.rope } else { &gesture.hud };
    let visible = !text.is_empty();
    // The source layer40 canvas draws nothing without a label; skip its pass.
    if let Some(mut camera) = hud.camera.and_then(|c| cameras.get_mut(c).ok())
        && camera.is_active != visible
    {
        camera.is_active = visible;
    }
    if !visible {
        if let Some(entity) = hud.entity {
            commands.entity(entity).insert(Visibility::Hidden);
        }
        hud.text.clear();
        return;
    }
    if hud.text == *text {
        if let Some(entity) = hud.entity {
            commands.entity(entity).insert(Visibility::Inherited);
        }
        return;
    }
    let Some(mut bitmap) = raster.render_fitted(text, 26., f32::MAX, 1.5, 0.9) else {
        // Never leave the previous text standing for a label that failed to raster.
        if let Some(entity) = hud.entity {
            commands.entity(entity).insert(Visibility::Hidden);
        }
        hud.text.clear();
        return;
    };
    bitmap.image.sampler = ImageSampler::linear();
    let size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32);
    let node = Node {
        position_type: PositionType::Absolute,
        left: percent(50.),
        top: px(90. + bitmap.origin.y),
        margin: UiRect::left(px(-300.
            + (600. - bitmap.layout.x).max(0.) * 0.5
            + bitmap.origin.x)),
        width: px(size.x),
        height: px(size.y),
        ..default()
    };
    if let Some(image) = &hud.image {
        *raster.images.get_mut(image).unwrap() = bitmap.image;
    } else {
        hud.image = Some(raster.images.add(bitmap.image));
    }
    if let Some(entity) = hud.entity {
        commands
            .entity(entity)
            .insert((node, Visibility::Inherited));
    } else {
        let camera = *hud.camera.get_or_insert_with(|| {
            commands
                .spawn((
                    Name::new("Source utility layer40"),
                    Camera2d,
                    Camera {
                        order: crate::ui::layers::TOOL_ORDER,
                        clear_color: ClearColorConfig::None,
                        ..default()
                    },
                    Hdr,
                    Tonemapping::None,
                ))
                .id()
        });
        hud.entity = Some(
            commands
                .spawn((
                    Name::new("Original wire-tool label"),
                    node,
                    ImageNode::new(hud.image.clone().unwrap()),
                    UiTargetCamera(camera),
                    GlobalZIndex(40),
                    bevy::picking::Pickable::IGNORE,
                ))
                .id(),
        );
    }
    hud.text = text.clone();
}
