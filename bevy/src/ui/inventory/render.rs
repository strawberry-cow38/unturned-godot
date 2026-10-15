//! Authored InventoryUI dashboard geometry and accepted item widgets.
use super::{
    controller::{Action, Menu},
    layout,
    model::*,
    paperdoll, tiles,
};
use crate::{
    character::nameplate::OriginalFont,
    player::GameSnapshot,
    settings::Settings,
    ui::{self, InventoryPage, Screen, UiModel, UiState},
    world::WorldConfig,
};
use bevy::{
    picking::{Pickable, hover::Hovered},
    prelude::*,
    ui_widgets::Button,
};
#[derive(Component)]
pub(super) struct Root;
#[derive(Component)]
pub(super) struct Ghost;
#[derive(Component)]
pub(super) struct Style;
#[derive(Resource, Default)]
pub(super) struct Art(pub tiles::Art);
fn text(
    p: &mut ChildSpawnerCommands,
    value: impl Into<String>,
    r: Rect,
    size: f32,
    c: Color,
    font: &Handle<Font>,
) {
    tiles::label(p, value, r, size, c, font, Justify::Left);
}
fn local(r: Rect, origin: Vec2) -> Rect {
    Rect::from_corners(r.min - origin, r.max - origin)
}
pub(super) fn button(
    p: &mut ChildSpawnerCommands,
    title: &str,
    r: Rect,
    action: Action,
    font: &Handle<Font>,
    _tooltip: &str,
) {
    let mut node = tiles::rect(r);
    node.border_radius = BorderRadius::all(px(3));
    node.align_items = AlignItems::Center;
    node.justify_content = JustifyContent::Center;
    p.spawn((
        node,
        Button,
        action,
        Style,
        Hovered::default(),
        BackgroundColor(Color::srgba(0.1, 0.1, 0.1, 0.6)),
    ))
    .with_child((
        Text::new(title),
        TextFont {
            font: font.clone().into(),
            font_size: FontSize::Px(16.),
            ..default()
        },
        TextColor(Color::srgb(0.875, 0.875, 0.875)),
        TextLayout::no_wrap(),
        Pickable::IGNORE,
    ));
}
pub(super) fn panel(
    p: &mut ChildSpawnerCommands,
    r: Rect,
    color: Color,
    f: impl FnOnce(&mut ChildSpawnerCommands),
) {
    p.spawn((tiles::rect(r), BackgroundColor(color), Pickable::IGNORE))
        .with_children(f);
}
#[derive(Clone)]
struct Section {
    page: u8,
    title: String,
    col: usize,
    y: f32,
    cloth: Option<u8>,
}
fn sections(menu: &mut Menu, model: &UiModel, dash: &layout::Dashboard) -> Vec<Section> {
    let mut result = vec![];
    let mut y = [0., 0.];
    for (page, title, cloth) in [
        (2, "Hands", None),
        (3, "Backpack", Some(5)),
        (4, "Vest", Some(4)),
        (5, "Shirt", Some(3)),
        (6, "Pants", Some(6)),
    ] {
        let Some(grid) = menu
            .pages
            .iter()
            .find(|p| p.index == page && p.width > 0 && p.height > 0)
        else {
            continue;
        };
        result.push(Section {
            page,
            title: cloth
                .and_then(|slot| {
                    menu.view
                        .tiles
                        .worn
                        .iter()
                        .find(|w| w.slot == slot)
                        .and_then(|w| w.tile.as_ref())
                })
                .map_or_else(|| title.into(), |t| t.name.clone()),
            col: 0,
            y: y[0],
            cloth,
        });
        y[0] += layout::page_advance(grid.height, menu.folded.contains(&page));
    }
    for (slot, title) in [(0, "Hat"), (2, "Mask"), (1, "Glasses")] {
        if menu
            .view
            .tiles
            .worn
            .iter()
            .any(|w| w.slot == slot && w.tile.is_some())
        {
            result.push(Section {
                page: 200 + slot,
                title: menu
                    .view
                    .tiles
                    .worn
                    .iter()
                    .find(|w| w.slot == slot)
                    .and_then(|w| w.tile.as_ref())
                    .map_or_else(|| title.into(), |t| t.name.clone()),
                col: 0,
                y: y[0],
                cloth: Some(slot),
            });
            y[0] += layout::HEADER_GAP;
        }
    }
    let col = usize::from(dash.split);
    for (page, title) in ui::storage::container_pages(model) {
        let grid = menu.pages.iter().find(|p| p.index == page);
        if grid.is_none_or(|p| p.width == 0 || p.height == 0) && page != AREA {
            continue;
        }
        result.push(Section {
            page,
            title,
            col,
            y: y[col],
            cloth: None,
        });
        y[col] += grid
            .filter(|p| p.width > 0 && p.height > 0)
            .map_or(layout::HEADER_GAP, |p| {
                p.height as f32 * CELL + layout::GRID_PAD + layout::PAGE_ADVANCE
            });
        if page == STORAGE
            && let Some(cooker) = &menu.view.cooker
        {
            y[col] += 34. + if cooker.needs_fuel { 14. } else { 0. };
        }
    }
    if !menu.view.quick_crafts.is_empty() {
        y[col] += layout::HEADER_GAP
            + CELL * (menu.view.quick_crafts.len().div_ceil(6).max(1) as f32)
            + layout::GRID_PAD;
    }
    menu.scroll.content = y;
    menu.scroll.clamp(dash);
    result
}
#[allow(clippy::too_many_arguments)]
fn header(
    p: &mut ChildSpawnerCommands,
    section: &Section,
    r: Rect,
    menu: &mut Menu,
    font: &Handle<Font>,
    art: &mut tiles::Art,
    config: &WorldConfig,
    images: &mut Assets<Image>,
    global: Vec2,
    clip: Rect,
) {
    panel(p, r, ui::navbar::neutral(0.17, 0.24, 0.32, 0.78), |p| {
        let width = r.width();
        let worn = section.cloth.and_then(|slot| {
            menu.view
                .tiles
                .worn
                .iter()
                .find(|w| w.slot == slot)
                .and_then(|w| w.tile.as_ref())
        });
        if let Some(tile) = worn {
            tiles::tile(
                p,
                tile,
                Rect::new(6., 6., 70., 70.),
                0,
                false,
                false,
                true,
                art,
                &config.content_root,
                images,
                font,
            );
            let hit = Rect::from_corners(
                global + r.min + Vec2::splat(6.),
                global + r.min + Vec2::splat(70.),
            );
            let hit = hit.intersect(clip);
            if !hit.is_empty() {
                menu.hits.header_icons.push((section.cloth.unwrap(), hit));
            }
            let q = tiles::color(&tile.quality_color, Color::WHITE).mix(&Color::WHITE, 0.3);
            tiles::label(
                p,
                if tile.broken {
                    "BROKEN".into()
                } else {
                    format!("{}%", tile.quality)
                },
                Rect::new(width - 116., 22., width - 22., 54.),
                30.,
                q,
                font,
                Justify::Right,
            );
        } else if let Some(page) = menu.pages.iter().find(|p| p.index == section.page)
            && !page.items.is_empty()
        {
            tiles::label(
                p,
                page.items.len().to_string(),
                Rect::new(width - 116., 22., width - 22., 54.),
                26.,
                ui::DIM,
                font,
                Justify::Right,
            );
        }
        tiles::label(
            p,
            &section.title,
            Rect::new(0., 18., width, 58.),
            34.,
            ui::TEXT,
            font,
            Justify::Center,
        );
        if section.page > 2 && section.page < 200 {
            // Original header's fold target covers its name/count but leaves the
            // worn-icon drag target at the left untouched.
            let b = Rect::new(76., 0., width, 76.);
            let mut node = tiles::rect(b);
            node.border_radius = BorderRadius::all(px(0));
            p.spawn((
                node,
                Button,
                Action::Fold(section.page),
                Hovered::default(),
                BackgroundColor(Color::NONE),
            ));
            let hit =
                Rect::from_corners(global + r.min + b.min, global + r.min + b.max).intersect(clip);
            if !hit.is_empty() {
                menu.hits.buttons.push(hit);
            }
        }
    });
}
#[allow(clippy::too_many_arguments)]
fn items(
    p: &mut ChildSpawnerCommands,
    page: &InventoryPage,
    origin: Vec2,
    clip: Rect,
    slot: bool,
    menu: &mut Menu,
    art: &mut tiles::Art,
    config: &WorldConfig,
    images: &mut Assets<Image>,
    font: &Handle<Font>,
    materials: &mut Assets<super::feedback::WheelMaterial>,
) {
    for item in &page.items {
        let cell = Cell::item(page.index, item);
        let Some(tile) = menu.view.tile(cell) else {
            continue;
        };
        let footprint = footprint(item, item.rot).as_vec2() * CELL;
        let offset = if slot {
            Vec2::ZERO
        } else {
            Vec2::new(item.x.into(), item.y.into()) * CELL
        };
        let bounds = Rect::from_corners(
            origin + offset,
            origin + offset + if slot { clip.size() } else { footprint },
        );
        let op = menu
            .magazines
            .operations
            .iter()
            .find(|op| op.magazine.cell() == cell);
        tiles::tile(
            p,
            tile,
            bounds,
            item.rot,
            menu.view.at(cell).is_some_and(|r| r.magazine),
            true,
            op.is_none(),
            art,
            &config.content_root,
            images,
            font,
        );

        if op.is_some() {
            super::feedback::spawn(p, cell, bounds, false, materials);
        }
        if menu.transfers.queue.iter().any(|(at, _)| *at == cell) {
            super::feedback::spawn(p, cell, bounds, true, materials);
        }
        if let Some((_, hint)) = menu.load_hint.as_ref().filter(|(at, _)| *at == cell) {
            let mut node = tiles::rect(bounds);
            node.border = UiRect::all(px(2));
            p.spawn((
                node,
                Pickable::IGNORE,
                BackgroundColor(if hint.allowed {
                    Color::srgba(0.30, 0.85, 0.40, 0.30)
                } else {
                    Color::srgba(0.90, 0.25, 0.22, 0.34)
                }),
                BorderColor::all(if hint.allowed {
                    Color::srgba(0.45, 1., 0.55, 0.90)
                } else {
                    Color::srgba(1., 0.42, 0.36, 0.95)
                }),
            ));
            tiles::label(
                p,
                if hint.allowed { "Load" } else { &hint.message },
                bounds,
                15.,
                if hint.allowed {
                    Color::srgb(0.72, 1., 0.78)
                } else {
                    Color::srgb(1., 0.72, 0.68)
                },
                font,
                Justify::Center,
            );
        }
    }
}
#[allow(clippy::too_many_arguments)]
pub(super) fn build(
    mut commands: Commands,
    ui: Res<UiState>,
    model: Res<UiModel>,
    snapshot: Res<GameSnapshot>,
    settings: Res<Settings>,
    config: Res<WorldConfig>,
    font: Res<OriginalFont>,
    preview: (Res<paperdoll::stage::Surface>, Res<super::badge::Badge>),
    mut menu: ResMut<Menu>,
    mut art: ResMut<Art>,
    mut images: ResMut<Assets<Image>>,
    roots: Query<(Entity, Option<&ui::swoop::Swoop>), With<Root>>,
    mut signature: Local<String>,
    mut numbers: Query<(
        Entity,
        &mut super::context::Number,
        &mut bevy::text::EditableText,
    )>,
    mut wheels: ResMut<Assets<super::feedback::WheelMaterial>>,
) {
    let (surface, badge) = preview;
    let next = format!(
        "{}|{}|{:?}|{:?}",
        menu.open, menu.revision, surface.material, settings.bindings
    );
    if *signature == next {
        return;
    }
    *signature = next;
    let editor = super::context::prepare(&mut commands, &mut numbers, &menu, &font.0);
    for (entity, animation) in &roots {
        ui::swoop::replace::<Root>(&mut commands, entity, animation, &ui, &model);
    }
    if !menu.open {
        return;
    }
    menu.hits = HitLayout {
        viewport: menu.viewport,
        ..default()
    };
    let dash = layout::Dashboard::new(menu.viewport);
    let sections = sections(&mut menu, &model, &dash);
    let font = &font.0;
    let viewport = menu.viewport;
    commands
        .spawn((
            Root,
            Node {
                width: percent(100),
                height: percent(100),
                ..default()
            },
            ui::swoop::Swoop(Screen::Inventory),
            ui::backdrop::Backdrop::dashboard(1.),
            GlobalZIndex(20),
            Pickable::IGNORE,
        ))
        .with_children(|p| {
            ui::navbar::build(
                p,
                viewport.x,
                &settings,
                Screen::Inventory,
                ui::map::available(&model.map_name),
                font,
            );
            menu.hits.buttons.push(Rect::new(0., 0., viewport.x, 60.));
            panel(
                p,
                dash.character,
                ui::navbar::neutral(0.10, 0.13, 0.18, 0.42),
                |p| {
                    panel(
                        p,
                        Rect::new(8., 6., dash.character.width() - 8., 82.),
                        ui::navbar::neutral(0.17, 0.24, 0.32, 0.78),
                        |p| {
                            let name = snapshot.0["player"]["profile"]["name"]
                                .as_str()
                                .filter(|s| !s.is_empty())
                                .unwrap_or("Survivor");
                            super::badge::draw(
                                p,
                                dash.character.width() - 16.,
                                name,
                                badge.picture.as_ref(),
                                font,
                            );
                        },
                    );
                    if let Some(material) = &surface.material {
                        p.spawn((
                            tiles::rect(local(dash.paperdoll, dash.character.min)),
                            MaterialNode(material.clone()),
                            ui::swoop::SwoopAlpha::default(),
                            Pickable::IGNORE,
                        ));
                    }
                    for (page, title, bounds) in [
                        (0, "PRIMARY", dash.primary),
                        (1, "SECONDARY", dash.secondary),
                    ] {
                        let r = local(bounds, dash.character.min);
                        text(
                            p,
                            title,
                            Rect::new(r.min.x, r.min.y - 24., r.max.x, r.min.y),
                            16.,
                            ui::TEXT,
                            font,
                        );
                        let mut node = tiles::rect(r);
                        node.overflow = Overflow::clip();
                        p.spawn((
                            node,
                            BackgroundColor(Color::srgba(0., 0., 0., 0.45)),
                            Pickable::IGNORE,
                        ))
                        .with_children(|p| {
                            if let Some(grid) = menu.pages.iter().find(|p| p.index == page).cloned()
                            {
                                items(
                                    p,
                                    &grid,
                                    Vec2::ZERO,
                                    Rect::from_corners(Vec2::ZERO, r.size()),
                                    true,
                                    &mut menu,
                                    &mut art.0,
                                    &config,
                                    &mut images,
                                    font,
                                    &mut wheels,
                                );
                            }
                        });
                        menu.hits.grids.push(Grid {
                            page,
                            rect: bounds,
                            clip: bounds,
                            slot: true,
                        });
                    }
                },
            );
            menu.hits.paperdoll = Some(dash.paperdoll);
            for col in 0..=usize::from(dash.split) {
                let origin = Vec2::new(
                    if col == 0 {
                        dash.storage.min.x
                    } else {
                        dash.container_x
                    },
                    dash.storage.min.y,
                );
                let bounds = Rect::from_corners(
                    origin,
                    origin + Vec2::new(dash.column_width, dash.storage.height()),
                );
                let mut node = tiles::rect(bounds);
                node.overflow = Overflow::clip();
                p.spawn((node, Pickable::IGNORE)).with_children(|p| {
                    for section in sections.iter().filter(|s| s.col == col) {
                        let y = section.y - menu.scroll.offset[col];
                        let header_rect =
                            Rect::new(0., y, dash.column_width.min(CELL * 8.), y + 76.);
                        header(
                            p,
                            section,
                            header_rect,
                            &mut menu,
                            font,
                            &mut art.0,
                            &config,
                            &mut images,
                            origin,
                            bounds,
                        );
                        let grid = menu.pages.iter().find(|p| p.index == section.page).cloned();
                        if let Some(page) = grid.filter(|p| p.width > 0 && p.height > 0) {
                            if !menu.folded.contains(&page.index) {
                                let grid_origin = Vec2::new(0., y + layout::HEADER_GAP);
                                let size = Vec2::new(page.width.into(), page.height.into()) * CELL;
                                p.spawn((
                                    tiles::rect(Rect::from_corners(
                                        grid_origin,
                                        grid_origin + size,
                                    )),
                                    Pickable::IGNORE,
                                ))
                                .with_children(|p| {
                                    tiles::grid(
                                        p,
                                        UVec2::new(page.width.into(), page.height.into()),
                                    );
                                    items(
                                        p,
                                        &page,
                                        Vec2::ZERO,
                                        Rect::from_corners(Vec2::ZERO, size),
                                        false,
                                        &mut menu,
                                        &mut art.0,
                                        &config,
                                        &mut images,
                                        font,
                                        &mut wheels,
                                    );
                                });
                                menu.hits.grids.push(Grid {
                                    page: page.index,
                                    rect: Rect::from_corners(
                                        origin + grid_origin,
                                        origin + grid_origin + size,
                                    ),
                                    clip: bounds,
                                    slot: false,
                                });
                            }
                            if page.index == STORAGE
                                && let Some(cooker) = menu.view.cooker.clone()
                            {
                                let y = y
                                    + page.height as f32 * CELL
                                    + layout::GRID_PAD
                                    + layout::PAGE_ADVANCE;
                                let r = Rect::new(0., y, dash.column_width, y + 30.);
                                button(
                                    p,
                                    &format!(
                                        "{} — {}",
                                        cooker.kind,
                                        if cooker.on { "ON" } else { "OFF" }
                                    ),
                                    r,
                                    Action::Cooker,
                                    font,
                                    "",
                                );
                                let hit = Rect::from_corners(origin + r.min, origin + r.max)
                                    .intersect(bounds);
                                if !hit.is_empty() {
                                    menu.hits.buttons.push(hit);
                                }
                                if cooker.needs_fuel {
                                    panel(
                                        p,
                                        Rect::new(0., y + 34., dash.column_width, y + 44.),
                                        ui::BUTTON,
                                        |p| {
                                            panel(
                                                p,
                                                Rect::new(
                                                    0.,
                                                    0.,
                                                    dash.column_width * cooker.fuel.clamp(0., 1.),
                                                    10.,
                                                ),
                                                Color::srgb(1., 0.55, 0.15),
                                                |_| {},
                                            );
                                        },
                                    );
                                }
                            }
                        }
                    }
                    if col == usize::from(dash.split) && !menu.view.quick_crafts.is_empty() {
                        let rows = menu.view.quick_crafts.len().div_ceil(6).max(1) as f32;
                        let y = menu.scroll.content[col]
                            - layout::HEADER_GAP
                            - rows * CELL
                            - layout::GRID_PAD
                            - menu.scroll.offset[col];
                        header(
                            p,
                            &Section {
                                page: 255,
                                title: "Quick Craft".into(),
                                col,
                                y,
                                cloth: None,
                            },
                            Rect::new(0., y, dash.column_width, y + 76.),
                            &mut menu,
                            font,
                            &mut art.0,
                            &config,
                            &mut images,
                            origin,
                            bounds,
                        );
                        tiles::label(
                            p,
                            menu.view.quick_crafts.len().to_string(),
                            Rect::new(
                                dash.column_width - 116.,
                                y + 22.,
                                dash.column_width - 22.,
                                y + 54.,
                            ),
                            26.,
                            ui::DIM,
                            font,
                            Justify::Right,
                        );
                        p.spawn((
                            tiles::at(0., y + layout::HEADER_GAP, 6. * CELL, rows * CELL),
                            Pickable::IGNORE,
                        ))
                        .with_children(|p| tiles::grid(p, UVec2::new(6, rows as u32)));
                        let crafts = menu.view.quick_crafts.clone();
                        for (i, craft) in crafts.iter().enumerate() {
                            let pos = Vec2::new(
                                (i % 6) as f32 * CELL,
                                y + layout::HEADER_GAP + (i / 6) as f32 * CELL,
                            );
                            let r = Rect::from_corners(pos, pos + Vec2::splat(CELL));
                            panel(p, r, ui::navbar::neutral(0.22, 0.29, 0.37, 0.62), |p| {
                                tiles::plain_icon(
                                    p,
                                    &craft.icon,
                                    Rect::new(6., 6., 66., 66.),
                                    &mut art.0,
                                    &config.content_root,
                                    &mut images,
                                );
                            });
                            let hit = Rect::from_corners(origin + r.min, origin + r.max)
                                .intersect(bounds);
                            if !hit.is_empty() {
                                menu.hits.quick_craft.push((craft.index, hit));
                                menu.hits
                                    .tooltips
                                    .push((hit, format!("{}\nLMB +1   RMB +5", craft.name)));
                            }
                        }
                    }
                });
            }
            super::scrollbar::draw(p, &mut menu, &dash);
            if let Some(cell) = menu.gesture.selected {
                super::context::draw(
                    p,
                    cell,
                    &mut menu,
                    &mut art.0,
                    &config,
                    &mut images,
                    font,
                    editor,
                );
            }
            super::tooltip::draw(p, font);
            if !menu.notice.is_empty() {
                text(
                    p,
                    &menu.notice,
                    Rect::new(24., viewport.y - 42., viewport.x - 24., viewport.y - 12.),
                    16.,
                    Color::srgb(0.95, 0.65, 0.45),
                    font,
                );
            }
        });
}
pub(super) fn appearance(
    mut buttons: Query<
        (
            &Hovered,
            Has<bevy::ui::Pressed>,
            &Children,
            &mut BackgroundColor,
        ),
        With<Style>,
    >,
    mut labels: Query<&mut TextColor>,
) {
    for (hover, pressed, children, mut color) in &mut buttons {
        color.0 = if pressed {
            Color::srgba(0., 0., 0., 0.6)
        } else if hover.get() {
            Color::srgba(0.225, 0.225, 0.225, 0.6)
        } else {
            Color::srgba(0.1, 0.1, 0.1, 0.6)
        };
        for child in children {
            if let Ok(mut text) = labels.get_mut(*child) {
                text.0 = if pressed {
                    Color::WHITE
                } else if hover.get() {
                    Color::srgb(0.95, 0.95, 0.95)
                } else {
                    Color::srgb(0.875, 0.875, 0.875)
                };
            }
        }
    }
}
pub(super) fn ghost(
    mut commands: Commands,
    menu: Res<Menu>,
    roots: Query<Entity, With<Ghost>>,
    mut art: ResMut<Art>,
    config: Res<WorldConfig>,
    mut images: ResMut<Assets<Image>>,
    font: Res<OriginalFont>,
    dashboard: Query<Entity, With<Root>>,
) {
    for root in &roots {
        commands.entity(root).despawn();
    }
    let Some(carry) = menu.gesture.carry.as_ref().filter(|_| menu.open) else {
        return;
    };
    let mut facts = match carry.source {
        Source::Grid(cell) => menu.view.tile(cell),
        Source::Worn(slot) => menu
            .view
            .tiles
            .worn
            .iter()
            .find(|w| w.slot == slot)
            .and_then(|w| w.tile.as_ref()),
    }
    .cloned()
    .unwrap_or_default();
    facts = super::money::preview(&facts, carry.item.amount, &menu.view.tiles.currency);
    let size = footprint(&carry.item, carry.rotation).as_vec2() * CELL;
    let origin = menu.gesture.pointer - carry.grab;
    let Some(parent) = dashboard.iter().next() else {
        return;
    };
    let ghost = commands
        .spawn((
            Ghost,
            ChildOf(parent),
            tiles::rect(Rect::from_corners(origin, origin + size)),
            GlobalZIndex(70),
            Pickable::IGNORE,
        ))
        .with_children(|p| {
            tiles::tile(
                p,
                &facts,
                Rect::from_corners(Vec2::ZERO, size),
                carry.rotation,
                menu.view
                    .items
                    .iter()
                    .any(|row| row.address.id == carry.item.id && row.magazine),
                false,
                true,
                &mut art.0,
                &config.content_root,
                &mut images,
                &font.0,
            );
        })
        .id();
    commands.queue(move |world: &mut World| {
        let children: Vec<_> = world
            .query::<&Children>()
            .query(world)
            .iter_descendants(ghost)
            .collect();
        for child in children {
            let mut entity = world.entity_mut(child);
            if let Some(mut c) = entity.get_mut::<BackgroundColor>() {
                c.0 = c.0.with_alpha(c.0.alpha() * 0.8);
            }
            if let Some(mut c) = entity.get_mut::<TextColor>() {
                c.0 = c.0.with_alpha(c.0.alpha() * 0.8);
            }
            if let Some(mut c) = entity.get_mut::<ImageNode>() {
                c.color = c.color.with_alpha(c.color.alpha() * 0.8);
            }
            if let Some(mut c) = entity.get_mut::<BorderColor>() {
                c.top = c.top.with_alpha(c.top.alpha() * 0.8);
                c.right = c.right.with_alpha(c.right.alpha() * 0.8);
                c.bottom = c.bottom.with_alpha(c.bottom.alpha() * 0.8);
                c.left = c.left.with_alpha(c.left.alpha() * 0.8);
            }
        }
    });
}
