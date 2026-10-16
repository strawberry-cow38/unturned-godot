//! Source selection panel and its linked split slider/number/preview controls.
use super::{
    controller::{Action, Menu},
    layout,
    model::*,
    render, tiles,
};
use crate::{
    ui::{Screen, UiState},
    world::WorldConfig,
};
use bevy::{
    input::{ButtonState, keyboard::KeyboardInput},
    input_focus::FocusedInput,
    picking::Pickable,
    prelude::*,
    text::{EditableText, FontCx, LayoutCx},
    ui_widgets::{
        Slider, SliderRange, SliderStep, SliderThumb, SliderValue, TextInput, TrackClick,
        ValueChange,
    },
};
#[derive(Component)]
pub(super) struct Number {
    pub cell: Cell,
    pub maximum: u16,
    pub requested: u16,
    pub editing: bool,
}
/// The source SpinBox embeds LineEdit, never a multiline text editor.
pub(super) fn number_text(value: u16) -> EditableText {
    let mut text = EditableText::new(value.to_string());
    text.allow_newlines = false;
    text
}
#[derive(Component)]
pub(super) struct SplitSlider;
#[derive(Component)]
pub(super) struct SplitThumb;
#[derive(Component)]
pub(super) struct SplitPreview(Option<super::api::Tile>);
#[derive(Component)]
pub(super) struct SplitFill;
#[derive(Component)]
pub(super) struct SliderArt([Handle<Image>; 2]);
pub(super) fn focus(
    focus: Res<bevy::input_focus::InputFocus>,
    fields: Query<(), With<Number>>,
    mut menu: ResMut<Menu>,
    mut typing: ResMut<super::Typing>,
) {
    menu.typing = focus.get().is_some_and(|entity| fields.contains(entity));
    typing.0 = menu.typing;
}
pub(super) fn prepare(
    commands: &mut Commands,
    fields: &mut Query<(Entity, &mut Number, &mut EditableText)>,
    menu: &Menu,
    font: &Handle<Font>,
) -> Option<Entity> {
    let selected = menu
        .gesture
        .selected
        .filter(|cell| cell.page != super::model::AREA)
        .and_then(|cell| {
            menu.view
                .tile(cell)
                .filter(|tile| tile.amount > 1)
                .map(|tile| (cell, tile.amount - 1))
        });
    let Some((cell, max)) = selected.filter(|_| menu.open) else {
        for (entity, _, _) in fields.iter_mut() {
            commands.entity(entity).despawn();
        }
        return None;
    };
    if let Some((entity, mut number, mut text)) = fields.iter_mut().next() {
        commands.entity(entity).remove::<ChildOf>();
        if number.cell != cell {
            text.clear();
            text.editor.set_text(&menu.gesture.split_amount.to_string());
        }
        let same = number.cell == cell;
        *number = Number {
            cell,
            maximum: max,
            requested: if same {
                number.requested
            } else {
                menu.gesture.split_amount
            },
            editing: same && number.editing,
        };
        return Some(entity);
    }
    Some(
        commands
            .spawn((
                Number {
                    cell,
                    maximum: max,
                    requested: menu.gesture.split_amount,
                    editing: false,
                },
                TextInput,
                bevy::input_focus::tab_navigation::TabIndex(0),
                number_text(menu.gesture.split_amount),
                tiles::at(0., 0., 84., 31.),
                TextFont {
                    font: font.clone().into(),
                    font_size: FontSize::Px(16.),
                    ..default()
                },
                TextColor(crate::ui::TEXT),
                TextLayout::no_wrap(),
                BackgroundColor(Color::srgba(0.09, 0.09, 0.10, 0.98)),
            ))
            .id(),
    )
}
pub(super) fn slider_change(
    event: On<ValueChange<f32>>,
    sliders: Query<(), With<SplitSlider>>,
    mut menu: ResMut<Menu>,
    ui: Res<UiState>,
) {
    if ui.screen == Screen::Inventory && sliders.contains(event.source) {
        menu.gesture.split_amount = event.value.round().clamp(1., u16::MAX as f32) as u16;
    }
}
pub(super) fn number_key(
    mut event: On<FocusedInput<KeyboardInput>>,
    mut fields: Query<(&mut Number, &mut EditableText)>,
    mut menu: ResMut<Menu>,
    ui: Res<UiState>,
    mut fonts: ResMut<FontCx>,
    mut layouts: ResMut<LayoutCx>,
    mut clipboard: ResMut<bevy::clipboard::Clipboard>,
) {
    if ui.screen != Screen::Inventory
        || event.event_target() != event.original_event_target()
        || event.input.state != ButtonState::Pressed
    {
        return;
    }
    let Ok((mut field, mut text)) = fields.get_mut(event.original_event_target()) else {
        return;
    };
    if text.is_composing()
        || !matches!(
            event.input.key_code,
            KeyCode::Enter | KeyCode::NumpadEnter | KeyCode::Escape
        )
    {
        return;
    }
    if event.input.key_code != KeyCode::Escape {
        // TextInput queues earlier keys/paste until PostUpdate. Enter belongs
        // to this event, so apply only edits already queued before evaluating.
        text.apply_pending_edits(&mut fonts, &mut layouts.0, &mut clipboard, |_| true);
        menu.gesture.split_amount = super::spin::committed(
            &text.value().to_string(),
            menu.gesture.split_amount,
            field.maximum,
        );
    }
    field.requested = menu.gesture.split_amount;
    text.clear();
    text.editor.set_text(&field.requested.to_string());
    event.propagate(false);
}
pub(super) fn synchronize(
    mut commands: Commands,
    mut menu: ResMut<Menu>,
    mut fields: Query<(Entity, &mut Number, &mut EditableText)>,
    mut thumbs: Query<&mut Node, (With<SplitThumb>, Without<SplitFill>)>,
    mut fills: Query<&mut Node, (With<SplitFill>, Without<SplitThumb>)>,
    sliders: Query<Entity, With<SplitSlider>>,
    mut focus: ResMut<bevy::input_focus::InputFocus>,
    mut fonts: ResMut<FontCx>,
    mut layouts: ResMut<LayoutCx>,
    mut clipboard: ResMut<bevy::clipboard::Clipboard>,
) {
    let Some(cell) = menu.gesture.selected else {
        return;
    };
    let Some(tile) = menu.view.tile(cell).filter(|t| t.amount > 1) else {
        return;
    };
    let total = tile.amount;
    let mut amount = menu.gesture.split_amount.clamp(1, total - 1);
    let requested = std::mem::take(&mut menu.spin.focus_requested);
    for (entity, mut field, mut text) in &mut fields {
        if requested {
            focus.set(entity, bevy::input_focus::FocusCause::Pressed);
        }
        let focused = focus.get() == Some(entity);
        let committed = field.editing && !focused;
        if committed {
            text.apply_pending_edits(&mut fonts, &mut layouts.0, &mut clipboard, |_| true);
            amount = super::spin::committed(&text.value().to_string(), amount, total - 1);
            menu.gesture.split_amount = amount;
        }
        if field.requested != amount || committed {
            text.clear();
            text.editor.set_text(&amount.to_string());
        }
        field.requested = amount;
        field.editing = focused;
    }
    let fraction = if total > 2 {
        (amount - 1) as f32 / (total - 2) as f32
    } else {
        0.5
    };
    for mut node in &mut thumbs {
        node.left = px((fraction * 314.).floor());
    }
    for mut fill in &mut fills {
        fill.width = px((fraction * 314.).floor() + 8.);
    }
    for entity in &sliders {
        commands.entity(entity).insert(SliderValue(amount as f32));
    }
}
/// Original RebuildSplitSlot replaces only its display child when the local
/// amount changes. It never rebuilds/focuses the sibling number editor.
pub(super) fn preview(
    mut commands: Commands,
    menu: Res<Menu>,
    mut previews: Query<(Entity, &mut SplitPreview, Option<&Children>)>,
    mut art: ResMut<render::Art>,
    config: Res<WorldConfig>,
    mut images: ResMut<Assets<Image>>,
    font: Res<crate::character::nameplate::OriginalFont>,
) {
    let Some(cell) = menu.gesture.selected else {
        return;
    };
    let Some(tile) = menu.view.tile(cell) else {
        return;
    };
    let facts = super::money::preview(tile, menu.gesture.split_amount, &menu.view.tiles.currency);
    let magazine = menu.view.at(cell).is_some_and(|row| row.magazine);
    for (entity, mut preview, children) in &mut previews {
        if preview.0.as_ref() == Some(&facts) {
            continue;
        }
        if let Some(children) = children {
            for child in children {
                commands.entity(*child).despawn();
            }
        }
        commands.entity(entity).with_children(|p| {
            tiles::tile(
                p,
                &facts,
                Rect::new(4., 4., 76., 76.),
                0,
                magazine,
                true,
                true,
                &mut art.0,
                &config.content_root,
                &mut images,
                &font.0,
            );
        });
        preview.0 = Some(facts.clone());
    }
}
#[allow(clippy::too_many_arguments)]
pub(super) fn draw(
    p: &mut ChildSpawnerCommands,
    cell: Cell,
    menu: &mut Menu,
    art: &mut tiles::Art,
    config: &WorldConfig,
    images: &mut Assets<Image>,
    font: &Handle<Font>,
    editor: Option<Entity>,
) {
    let (Some(row), Some(tile), Some(item)) = (
        menu.view.at(cell).cloned(),
        menu.view.tile(cell).cloned(),
        super::model::at(&menu.pages, cell).cloned(),
    ) else {
        return;
    };
    let split = tile.amount > 1 && row.address.page != super::model::AREA;
    let extra: f32 = if split { 166. } else { 0. };
    let height = (300. + extra)
        .max(150. + row.actions.len().saturating_sub(1) as f32 * 44. + 36. + 14. + extra);
    let size = Vec2::new(500., height);
    let origin = ((menu.viewport - size) * 0.5).round();
    let bounds = Rect::from_corners(origin, origin + size);
    menu.hits.context = Some(bounds);
    let mut node = tiles::rect(bounds);
    node.border_radius = BorderRadius::all(px(3));
    p.spawn((
        node,
        GlobalZIndex(50),
        BackgroundColor(crate::ui::navbar::neutral(0.10, 0.13, 0.18, 0.42)),
        Pickable::IGNORE,
    ))
    .with_children(|p| {
        let natural = footprint(&item, item.rot).as_vec2() * CELL;
        let scale = (Vec2::new(200., 280.) / natural).min_element().min(2.);
        let preview = Rect::from_center_size(Vec2::new(110., 150.), natural * scale);
        tiles::tile(
            p,
            &tile,
            preview,
            item.rot,
            row.magazine,
            true,
            true,
            art,
            &config.content_root,
            images,
            font,
        );
        let rarity = tiles::color(&row.rarity_color, Color::WHITE);
        tiles::label(
            p,
            &row.name,
            Rect::new(228., 14., 486., 42.),
            20.,
            rarity,
            font,
            Justify::Left,
        );
        tiles::label(
            p,
            &row.info,
            Rect::new(228., 46., 486., 66.),
            12.,
            rarity.mix(&crate::ui::DIM, 0.5),
            font,
            Justify::Left,
        );
        let description = tiles::label(
            p,
            &row.description,
            Rect::new(228., 72., 486., 142.),
            13.,
            crate::ui::TEXT,
            font,
            Justify::Left,
        );
        p.commands()
            .entity(description)
            .insert(TextLayout::new(Justify::Left, LineBreak::WordBoundary));
        let condition = if let Some(fluid) = &row.fluid {
            fluid.contents.as_str()
        } else {
            &row.condition
        };
        if !condition.is_empty() {
            tiles::label(
                p,
                condition,
                Rect::new(228., 120., 486., 142.),
                13.,
                tiles::color(&row.condition_color, crate::ui::DIM),
                font,
                Justify::Left,
            );
        }
        for (i, action) in row.actions.iter().enumerate() {
            let r = Rect::new(228., 150. + i as f32 * 44., 486., 186. + i as f32 * 44.);
            render::button(
                p,
                &action.label,
                r,
                if action.action == "close" {
                    Action::CloseContext
                } else {
                    Action::Context(action.action.clone())
                },
                font,
                &action.tooltip,
            );
            let hit = Rect::from_corners(origin + r.min, origin + r.max);
            menu.hits.buttons.push(hit);
            if !action.tooltip.is_empty() {
                menu.hits.tooltips.push((hit, action.tooltip.clone()));
            }
        }
        if split {
            let top = height - 166. + 8.;
            tiles::label(
                p,
                "Split",
                Rect::new(20., top, 140., top + 22.),
                16.,
                crate::ui::TEXT,
                font,
                Justify::Left,
            );
            let slider = Rect::new(20., top + 30., 350., top + 50.);
            let slider_art = art
                .slider
                .get_or_insert_with(|| {
                    use bevy::{
                        asset::RenderAssetUsages,
                        image::{CompressedImageFormats, ImageSampler, ImageType},
                    };
                    [
                        include_bytes!("../../../assets/ui/slider_grabber.png").as_slice(),
                        include_bytes!("../../../assets/ui/slider_grabber_highlight.png")
                            .as_slice(),
                    ]
                    .map(|bytes| {
                        images.add(
                            Image::from_buffer(
                                bytes,
                                ImageType::Extension("png"),
                                CompressedImageFormats::NONE,
                                true,
                                ImageSampler::linear(),
                                RenderAssetUsages::default(),
                            )
                            .unwrap(),
                        )
                    })
                })
                .clone();
            p.spawn((
                tiles::rect(slider),
                SplitSlider,
                bevy::picking::hover::Hovered::default(),
                SliderArt(slider_art.clone()),
                Slider {
                    track_click: TrackClick::Snap,
                    ..default()
                },
                SliderValue(menu.gesture.split_amount as f32),
                SliderRange::new(1., (tile.amount - 1) as f32),
                SliderStep(1.),
            ))
            .with_children(|p| {
                let mut track = tiles::at(0., 6., 330., 8.);
                track.border_radius = BorderRadius::all(px(4));
                p.spawn((
                    track.clone(),
                    BackgroundColor(Color::srgba(0.1, 0.1, 0.1, 0.6)),
                    Pickable::IGNORE,
                ));
                track.width = px(8.);
                p.spawn((
                    track,
                    SplitFill,
                    BackgroundColor(Color::srgba(1., 1., 1., 0.4)),
                    Pickable::IGNORE,
                ));
                p.spawn((
                    tiles::at(0., 2., 16., 16.),
                    SplitThumb,
                    SliderThumb,
                    ImageNode::new(slider_art[0].clone()),
                    Pickable::IGNORE,
                ));
            });
            menu.hits
                .buttons
                .push(Rect::from_corners(origin + slider.min, origin + slider.max));
            let field = Rect::new(364., top + 14., 464., top + 45.);
            menu.hits.number = Some(Rect::from_corners(origin + field.min, origin + field.max));
            super::spin::draw(p, field, menu, art, images);
            if let Some(editor) = editor {
                p.commands().entity(editor).insert(tiles::rect(Rect::new(
                    field.min.x,
                    field.min.y,
                    field.max.x - 16.,
                    field.max.y,
                )));
                let parent = p.target_entity();
                p.commands().entity(editor).insert(ChildOf(parent));
            }
            menu.hits
                .buttons
                .push(Rect::from_corners(origin + field.min, origin + field.max));
            for (n, name) in layout::split_marks(tile.amount) {
                let fraction = if tile.amount > 2 {
                    (n - 1) as f32 / (tile.amount - 2) as f32
                } else {
                    0.5
                };
                let x = 20. + fraction * 330.;
                render::panel(
                    p,
                    Rect::new(x, top + 52., x + 2., top + 60.),
                    crate::ui::DIM,
                    |_| {},
                );
                tiles::label(
                    p,
                    name,
                    Rect::new(x - 20., top + 60., x + 20., top + 78.),
                    12.,
                    crate::ui::DIM,
                    font,
                    Justify::Center,
                );
            }
            let preview = Rect::new(364., top + 48., 444., top + 128.);
            p.spawn((
                tiles::rect(preview),
                BackgroundColor(Color::srgba(0.1, 0.1, 0.1, 0.6)),
                Pickable::IGNORE,
                SplitPreview(None),
            ));
            menu.hits.split_preview = Some(Rect::from_corners(
                origin + preview.min,
                origin + preview.max,
            ));
            tiles::label(
                p,
                "drag out",
                Rect::new(354., top + 130., 454., top + 148.),
                12.,
                crate::ui::DIM,
                font,
                Justify::Center,
            );
        }
    });
}

pub(super) fn slider_appearance(
    sliders: Query<(
        &bevy::picking::hover::Hovered,
        Has<bevy::ui::Pressed>,
        &SliderArt,
        &Children,
    )>,
    mut fills: Query<&mut BackgroundColor, With<SplitFill>>,
    mut thumbs: Query<&mut ImageNode, With<SplitThumb>>,
) {
    for (hover, pressed, art, children) in &sliders {
        let live = hover.get() || pressed;
        for child in children {
            if let Ok(mut fill) = fills.get_mut(*child) {
                fill.0 = Color::srgba(1., 1., 1., if live { 0.75 } else { 0.4 });
            }
            if let Ok(mut image) = thumbs.get_mut(*child) {
                image.image = art.0[usize::from(live)].clone();
            }
        }
    }
}
