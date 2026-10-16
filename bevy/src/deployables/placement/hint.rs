//! `HUD.PlacementHint`: why the ghost is red, in warning amber 46 px below the
//! crosshair (22 px font, black outline_size 6), shown only while aiming.
use super::*;
use crate::character::nameplate::raster::{OriginalFont, Raster};
use bevy::{image::ImageSampler, text::TextPipeline};

pub(super) const AMBER: Color = Color::srgb(0.98, 0.72, 0.28);
#[derive(Resource, Default)]
pub(crate) struct Hint {
    pub(crate) text: String,
    entity: Option<Entity>,
    image: Option<Handle<Image>>,
}
pub(super) fn install(app: &mut App) {
    if !app.world().contains_resource::<Assets<Font>>() {
        app.init_asset::<Font>();
    }
    app.init_resource::<OriginalFont>()
        .init_resource::<Hint>()
        .add_systems(
            Update,
            draw.after(super::act)
                .run_if(resource_exists::<TextPipeline>),
        );
}
fn draw(
    mut commands: Commands,
    placer: Res<Placer>,
    mut hint: ResMut<Hint>,
    mut raster: Raster,
    labels: Query<(), With<ImageNode>>,
) {
    if hint.entity.is_some_and(|e| !labels.contains(e)) {
        hint.entity = None;
        hint.text.clear();
    }
    let text = placer.hint().unwrap_or("");
    if text.is_empty() {
        if let Some(entity) = hint.entity {
            commands.entity(entity).insert(Visibility::Hidden);
        }
        hint.text.clear();
        return;
    }
    if hint.text == text {
        if let Some(entity) = hint.entity {
            commands.entity(entity).insert(Visibility::Inherited);
        }
        return;
    }
    let Some(mut bitmap) = raster.render_fitted(text, 22., f32::MAX, 1.5, 1.) else {
        if let Some(entity) = hint.entity {
            commands.entity(entity).insert(Visibility::Hidden);
        }
        hint.text.clear();
        return;
    };
    bitmap.image.sampler = ImageSampler::linear();
    let size = Vec2::new(bitmap.image.width() as f32, bitmap.image.height() as f32);
    // Full-width label anchored at mid-screen, OffsetTop 46, centred text.
    let node = Node {
        position_type: PositionType::Absolute,
        left: percent(50.),
        top: percent(50.),
        margin: UiRect {
            left: px(-bitmap.layout.x * 0.5 + bitmap.origin.x),
            top: px(46. + bitmap.origin.y),
            ..default()
        },
        width: px(size.x),
        height: px(size.y),
        ..default()
    };
    if let Some(image) = &hint.image {
        *raster.images.get_mut(image).unwrap() = bitmap.image;
    } else {
        hint.image = Some(raster.images.add(bitmap.image));
    }
    if let Some(entity) = hint.entity {
        commands.entity(entity).insert((node, Visibility::Inherited));
    } else {
        hint.entity = Some(
            commands
                .spawn((
                    Name::new("Original placement hint"),
                    node,
                    ImageNode::new(hint.image.clone().unwrap()).with_color(AMBER),
                    GlobalZIndex(21),
                    bevy::picking::Pickable::IGNORE,
                ))
                .id(),
        );
    }
    hint.text = text.into();
}
