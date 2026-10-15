use super::*;
mod padlock;
#[cfg(test)]
pub(super) mod tests;
// CraftingMenu deliberately keeps these solid authored colors outside Neutral.
const BG_SOLID: Color = Color::srgba(0.13, 0.13, 0.14, 0.96);
const BAR_SOLID: Color = Color::srgba(0.19, 0.19, 0.20, 0.98);
const SELECTED: Color = Color::srgba(0.34, 0.36, 0.38, 0.98);
const TILE: Color = Color::srgba(0.22, 0.22, 0.23, 0.98);
const UNAVAILABLE: Color = Color::srgba(0.15, 0.15, 0.16, 0.96);
const DISABLED_TEXT: Color = Color::srgb(0.40, 0.41, 0.45);
const GOOD: Color = Color::srgb(0.62, 0.82, 0.60);
const BAD: Color = Color::srgb(0.86, 0.52, 0.46);
// Installed Godot4.6.2 default ThemeDB, used by source unstyled Panel/Button.
const DEFAULT_PANEL: Color = Color::srgba(0.1, 0.1, 0.1, 0.6);
#[derive(Component)]
pub(super) struct ControlStyle {
    enabled: bool,
    field: bool,
    flat: bool,
}
#[derive(Component)]
pub(super) struct QueueStrip;
#[derive(Component)]
pub(super) struct QueueArea;

#[derive(Component)]
pub(super) struct Root;
#[derive(Component)]
pub(super) struct Tooltip(String);
#[derive(Component)]
pub(super) struct TooltipText;
#[derive(Clone, Copy)]
pub(super) struct Layout {
    pub categories: Rect,
    pub grid: Rect,
    pub queue: Rect,
    pub queue_row: Rect,
    detail: Rect,
    search: Rect,
    header: Rect,
    pub scale: f32,
    tile: f32,
    columns: usize,
}
impl Layout {
    pub(super) fn new(view: Vec2) -> Self {
        let scale = (view.x / 2560.).clamp(0.5, 1.);
        let top = 100.;
        let cat_w = view.x * 0.23;
        let grid_x = 24. + cat_w + 16.;
        let detail_x = view.x - 32. - 600. * scale;
        let grid_w = (detail_x - grid_x - 16.).max(132. * scale);
        let queue_y = view.y - 16. - 142. * scale;
        let search_y = queue_y - 62. * scale;
        let header_bottom =
            vitals::content_bottom(view, 24., 24. + cat_w, view.y - 16. - 150. * scale);
        let tile = 116. * scale;
        Self {
            categories: Rect::new(
                24.,
                top,
                24. + cat_w,
                (header_bottom - 60. * scale - 10.).max(top + 90.),
            ),
            grid: Rect::new(
                grid_x,
                top,
                grid_x + grid_w,
                (search_y - 12. * scale).max(top + 90.),
            ),
            queue: Rect::new(grid_x, queue_y, grid_x + grid_w, queue_y + 126. * scale),
            queue_row: Rect::new(
                grid_x + 200. * scale,
                queue_y + 4. * scale,
                grid_x + grid_w - 8. * scale,
                queue_y + 122. * scale,
            ),
            detail: Rect::new(detail_x, top, view.x - 32., view.y - 32.),
            search: Rect::new(
                grid_x,
                search_y,
                grid_x + cat_w.min(grid_w),
                search_y + 52. * scale,
            ),
            header: Rect::new(24., header_bottom - 60. * scale, 24. + cat_w, header_bottom),
            scale,
            tile,
            columns: (((grid_w - 16. * scale + 8. * scale) / (tile + 8. * scale)).floor() as usize)
                .max(5),
        }
    }
}
fn rect(bounds: Rect) -> Node {
    Node {
        position_type: PositionType::Absolute,
        left: px(bounds.min.x),
        top: px(bounds.min.y),
        width: px(bounds.width()),
        height: px(bounds.height()),
        ..default()
    }
}
fn at(x: f32, y: f32, w: f32, h: f32) -> Node {
    rect(Rect::new(x, y, x + w, y + h))
}
fn label(
    p: &mut ChildSpawnerCommands,
    value: impl Into<String>,
    bounds: Rect,
    size: f32,
    color: Color,
    font: &Handle<Font>,
) {
    let value = value.into();
    let nowrap = matches!(value.as_str(), "CRAFTING QUEUE" | "(empty)");
    p.spawn((
        rect(bounds),
        Text::new(value),
        TextFont {
            font: font.clone().into(),
            font_size: FontSize::Px(size),
            ..default()
        },
        TextColor(color),
        if nowrap {
            TextLayout::no_wrap()
        } else {
            TextLayout::new(Justify::Left, LineBreak::WordBoundary)
        },
        Pickable::IGNORE,
    ));
}
fn control(
    p: &mut ChildSpawnerCommands,
    value: impl Into<String>,
    bounds: Rect,
    action: Action,
    enabled: bool,
    _active: bool,
    font: &Handle<Font>,
    size: f32,
) {
    let field = false;
    let mut node = rect(bounds);
    node.justify_content = if field {
        JustifyContent::FlexStart
    } else {
        JustifyContent::Center
    };
    if field {
        node.padding.left = px(6.);
    }

    node.align_items = AlignItems::Center;
    node.border_radius = BorderRadius::all(px(4));
    let mut entity = p.spawn((
        node,
        action,
        ControlStyle {
            enabled,
            field,
            flat: false,
        },
        BackgroundColor(if field {
            Color::srgba(0.09, 0.09, 0.10, 0.98)
        } else if enabled {
            DEFAULT_PANEL
        } else {
            Color::srgba(0.1, 0.1, 0.1, 0.3)
        }),
    ));
    if enabled {
        entity.insert((Button, Hovered::default()));
    }
    entity.with_children(|p| {
        p.spawn((
            Text::new(value),
            TextFont {
                font: font.clone().into(),
                font_size: FontSize::Px(size),
                ..default()
            },
            TextColor(if enabled { TEXT } else { DISABLED_TEXT }),
            Pickable::IGNORE,
        ));
    });
}
fn icon(
    p: &mut ChildSpawnerCommands,
    path: &str,
    bounds: Rect,
    enabled: bool,
    assets: &AssetServer,
) {
    if path.is_empty() {
        return;
    }
    let mut image = ImageNode::new(assets.load(path.to_owned()));
    if !enabled {
        image.color = Color::srgba(0.55, 0.55, 0.58, 0.55);
    }
    p.spawn((rect(bounds), image, Pickable::IGNORE));
}
fn category(
    p: &mut ChildSpawnerCommands,
    title: &str,
    count: i32,
    bounds: Rect,
    action: Action,
    selected: bool,
    clip: bool,
    font: &Handle<Font>,
    scale: f32,
) {
    p.spawn((
        rect(bounds),
        BackgroundColor(if selected { SELECTED } else { DEFAULT_PANEL }),
    ))
    .with_children(|p| {
        p.spawn((
            at(0., 0., bounds.width() - 72. * scale, bounds.height()),
            Button,
            Hovered::default(),
            action,
            ControlStyle {
                enabled: true,
                field: false,
                flat: true,
            },
            BackgroundColor(Color::NONE),
        ))
        .with_children(|p| {
            let mut node = at(
                8. * scale,
                0.,
                bounds.width() - 80. * scale,
                bounds.height(),
            );
            node.align_items = AlignItems::Center;
            if clip {
                node.overflow = Overflow::clip();
            }
            p.spawn(node).with_children(|p| {
                p.spawn((
                    Text::new(title),
                    TextFont {
                        font: font.clone().into(),
                        font_size: FontSize::Px(26. * scale),
                        ..default()
                    },
                    TextColor(TEXT),
                    TextLayout::no_wrap(),
                    Pickable::IGNORE,
                ));
            });
        });
        let mut node = at(
            bounds.width() - 72. * scale,
            0.,
            58. * scale,
            bounds.height(),
        );
        node.justify_content = JustifyContent::FlexEnd;
        node.align_items = AlignItems::Center;
        p.spawn((node, Pickable::IGNORE)).with_child((
            Text::new(count.to_string()),
            TextFont {
                font: font.clone().into(),
                font_size: FontSize::Px(26. * scale),
                ..default()
            },
            TextColor(DIM),
            Pickable::IGNORE,
        ));
    });
}
fn lock(p: &mut ChildSpawnerCommands, bounds: Rect, handle: &Handle<Image>) {
    let margin = Vec2::splat(bounds.width() / (116. * 0.36));
    p.spawn((
        rect(Rect::from_corners(bounds.min - margin, bounds.max + margin)),
        ImageNode::new(handle.clone()),
        Pickable::IGNORE,
    ));
}
fn draw_details(
    p: &mut ChildSpawnerCommands,
    selected: &Value,
    l: Layout,
    font: &Handle<Font>,
    assets: &AssetServer,
) {
    p.spawn((rect(l.detail), BackgroundColor(BG_SOLID)))
        .with_children(|p| {
            let width = l.detail.width();
            let height = l.detail.height();
            let pad = 16. * l.scale;
            if selected.is_null() {
                label(
                    p,
                    "select a recipe",
                    Rect::new(pad, pad, width - pad, 60.),
                    23. * l.scale,
                    DIM,
                    font,
                );
                return;
            }
            let image = 128. * l.scale;
            icon(
                p,
                &s(selected, "icon"),
                Rect::new(pad, pad, pad + image, pad + image),
                true,
                assets,
            );
            let name_x = pad + image + 14. * l.scale;
            label(
                p,
                s(selected, "name"),
                Rect::new(name_x, pad, width - pad, pad + image - 28. * l.scale),
                34. * l.scale,
                TEXT,
                font,
            );
            if i(selected, "outputAmount") > 1 {
                label(
                    p,
                    format!("makes x{}", i(selected, "outputAmount")),
                    Rect::new(
                        name_x,
                        pad + image - 26. * l.scale,
                        width - pad,
                        pad + image,
                    ),
                    18. * l.scale,
                    DIM,
                    font,
                );
            }
            let mut y = pad + image + 14. * l.scale;
            for (show, value, color) in [
                (selected["known"] == false, "LOCKED".into(), BAD),
                (
                    !s(selected, "skill").is_empty(),
                    format!(
                        "requires {} {}",
                        s(selected, "skill"),
                        i(selected, "skillLevel")
                    ),
                    if selected["meetsSkill"] == true {
                        DIM
                    } else {
                        BAD
                    },
                ),
                (
                    selected["requiresStation"] == true,
                    "requires a crafting station".into(),
                    DIM,
                ),
            ] {
                if show {
                    label(
                        p,
                        value,
                        Rect::new(pad, y, width - pad, y + 30. * l.scale),
                        23. * l.scale,
                        color,
                        font,
                    );
                    y += 30. * l.scale;
                }
            }
            let rows = selected["ingredients"].as_array().map_or(0, Vec::len);
            let controls_y = height - pad - 60. * l.scale;
            let reason_y = controls_y
                - if s(selected, "reason").is_empty() {
                    6. * l.scale
                } else {
                    42. * l.scale
                };
            let table_y = (reason_y - (rows as f32 + 1.) * 30. * l.scale - 10. * l.scale)
                .max(y + 30. * l.scale);
            label(
                p,
                s(selected, "description"),
                Rect::new(
                    pad,
                    y,
                    width - pad,
                    (table_y - 10. * l.scale).max(y + 20. * l.scale),
                ),
                23. * l.scale,
                Color::srgb(0.79, 0.79, 0.79),
                font,
            );
            // Source GridContainer keeps AMOUNT/TOTAL/HAVE at their text minima,
            // with fourteen pixels between columns; ITEM TYPE receives the rest.
            let x = [
                pad,
                pad + 96. * l.scale,
                width - pad - 146. * l.scale,
                width - pad - 66. * l.scale,
                width - pad,
            ];
            for (col, title) in ["AMOUNT", "ITEM TYPE", "TOTAL", "HAVE"].iter().enumerate() {
                label(
                    p,
                    *title,
                    Rect::new(
                        x[col],
                        table_y,
                        x[col + 1] - 14. * l.scale,
                        table_y + 24. * l.scale,
                    ),
                    18. * l.scale,
                    DIM,
                    font,
                );
            }
            for (row, ingredient) in selected["ingredients"]
                .as_array()
                .into_iter()
                .flatten()
                .enumerate()
            {
                let y = table_y + (row as f32 + 1.) * 30. * l.scale;
                let color = if i(ingredient, "have") >= i(ingredient, "total") {
                    GOOD
                } else {
                    BAD
                };
                let values = [
                    i(ingredient, "amount").to_string(),
                    format!(
                        "{}{}",
                        s(ingredient, "name"),
                        if ingredient["consume"] == false {
                            "  (tool)"
                        } else {
                            ""
                        }
                    ),
                    if ingredient["consume"] == true {
                        i(ingredient, "total").to_string()
                    } else {
                        "-".into()
                    },
                    i(ingredient, "have").to_string(),
                ];
                for (col, value) in values.into_iter().enumerate() {
                    label(
                        p,
                        value,
                        Rect::new(x[col], y, x[col + 1] - 14. * l.scale, y + 30. * l.scale),
                        23. * l.scale,
                        color,
                        font,
                    );
                }
            }
            label(
                p,
                s(selected, "reason"),
                Rect::new(pad, reason_y, width - pad, controls_y - 8. * l.scale),
                23. * l.scale,
                BAD,
                font,
            );
            let b = 60. * l.scale;
            let gap = 8. * l.scale;
            control(
                p,
                "−",
                Rect::new(pad, controls_y, pad + b, controls_y + b),
                Action::Quantity(-1),
                i(selected, "quantity") > 1,
                false,
                font,
                26. * l.scale,
            );
            label(
                p,
                i(selected, "quantity").to_string(),
                Rect::new(
                    pad + b + gap,
                    controls_y,
                    pad + b + gap + 76. * l.scale,
                    controls_y + b,
                ),
                30. * l.scale,
                TEXT,
                font,
            );
            let plus = pad + b + gap + 76. * l.scale + gap;
            control(
                p,
                "+",
                Rect::new(plus, controls_y, plus + b, controls_y + b),
                Action::Quantity(1),
                i(selected, "quantity") < i(selected, "maxQuantity"),
                false,
                font,
                26. * l.scale,
            );
            control(
                p,
                "CRAFT",
                Rect::new(plus + b + gap, controls_y, width - pad, controls_y + b),
                Action::Craft,
                selected["craftable"] == true,
                false,
                font,
                26. * l.scale,
            );
        });
}
pub(super) fn build(
    mut commands: Commands,
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    mut menu: ResMut<Menu>,
    assets: Res<AssetServer>,
    font: Res<crate::character::nameplate::OriginalFont>,
    mut images: ResMut<Assets<Image>>,
    mut locks: Local<padlock::Cache>,
    roots: Query<(Entity, Option<&swoop::Swoop>), With<Root>>,
    mut fields: Query<(
        Entity,
        &mut bevy::text::EditableText,
        &mut search::History,
        &mut search::Field,
    )>,
    mut signature: Local<String>,
) {
    if ui.screen != Screen::Crafting {
        for (root, animation) in &roots {
            swoop::replace::<Root>(&mut commands, root, animation, &ui, &model);
        }
        signature.clear();
        return;
    }
    let l = Layout::new(menu.viewport);
    let view = &menu.options["view"];
    let lookup = !s(view, "lookupLabel").is_empty();
    let ncat = view["categories"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|cat| i(cat, "count") > 0)
        .count()
        + usize::from(lookup);
    let ntiles = view["tiles"].as_array().map_or(0, Vec::len);
    let nqueue = menu.options["queue"].as_array().map_or(0, Vec::len);
    let max = [
        (ncat as f32 * 58. * l.scale - 2. * l.scale - l.categories.height()).max(0.),
        (ntiles.div_ceil(l.columns) as f32 * (l.tile + 8. * l.scale) - l.grid.height()).max(0.),
        (nqueue as f32 * 92. * l.scale - l.queue_row.width()).max(0.),
    ];
    for (scroll, max) in menu.scroll.iter_mut().zip(max) {
        *scroll = scroll.clamp(0., max);
    }
    if menu.retain_queue_widgets() && !roots.is_empty() {
        return;
    }
    let key = format!(
        "{}|{:?}|{:?}|{}|{:?}|{:?}",
        menu.options,
        menu.browse,
        [menu.scroll[0], menu.scroll[1]],
        menu.typing,
        menu.viewport,
        settings.bindings
    );
    if *signature == key && !roots.is_empty() {
        return;
    }
    *signature = key;
    let field = search::prepare(
        &mut commands,
        &mut fields,
        &menu.browse.search,
        l.search,
        &font.0,
        l.scale,
    );
    for (root, animation) in &roots {
        swoop::replace::<Root>(&mut commands, root, animation, &ui, &model);
    }
    let view = &menu.options["view"];
    let lock_image = locks.get(Vec2::new(l.tile * 0.36, l.tile * 0.43), &mut images);
    let root = commands
        .spawn((
            Root,
            Node {
                position_type: PositionType::Absolute,
                width: percent(100),
                height: percent(100),
                ..default()
            },
            GlobalZIndex(20),
            backdrop::Backdrop::dashboard(0.),
            swoop::Swoop(Screen::Crafting),
            UiTransform::default(),
        ))
        .with_children(|p| {
            p.spawn((
                rect(Rect::from_corners(
                    Vec2::splat(16.),
                    menu.viewport - Vec2::splat(16.),
                )),
                BackgroundColor(navbar::neutral(0.10, 0.13, 0.18, 0.42)),
                Pickable::IGNORE,
            ));
            navbar::build(
                p,
                menu.viewport.x,
                &settings,
                Screen::Crafting,
                map::available(&model.map_name),
                &font.0,
            );
            label(p, s(view, "header"), l.header, 20. * l.scale, DIM, &font.0);
            p.spawn(
                (Node {
                    overflow: Overflow::clip(),
                    ..rect(l.categories)
                }),
            )
            .with_children(|p| {
                let mut row = 0;
                if lookup {
                    let y = -menu.scroll[0];
                    category(
                        p,
                        &format!("×  {}", s(view, "lookupLabel")),
                        ntiles as i32,
                        Rect::new(0., y, l.categories.width(), y + 56. * l.scale),
                        Action::Clear,
                        true,
                        true,
                        &font.0,
                        l.scale,
                    );
                    row += 1;
                }
                for cat in view["categories"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter(|cat| i(cat, "count") > 0)
                {
                    let y = row as f32 * 58. * l.scale - menu.scroll[0];
                    let name = s(cat, "name");
                    category(
                        p,
                        &name,
                        i(cat, "count"),
                        Rect::new(0., y, l.categories.width(), y + 56. * l.scale),
                        Action::Category(name.clone()),
                        !lookup && menu.browse.category == name,
                        false,
                        &font.0,
                        l.scale,
                    );
                    row += 1;
                }
            });
            p.spawn(
                (Node {
                    overflow: Overflow::clip(),
                    ..rect(l.grid)
                }),
            )
            .with_children(|p| {
                for (index, tile) in view["tiles"].as_array().into_iter().flatten().enumerate() {
                    let x = (index % l.columns) as f32 * (l.tile + 8. * l.scale);
                    let y = (index / l.columns) as f32 * (l.tile + 8. * l.scale) - menu.scroll[1];
                    let selected = i(tile, "index") == menu.browse.selected;
                    let can = tile["craftable"] == true;
                    let mut node = at(x, y, l.tile, l.tile);
                    node.border_radius = BorderRadius::all(px(4. * l.scale));
                    if can {
                        node.border = UiRect::all(px(2. * l.scale));
                    }
                    p.spawn((
                        node,
                        Button,
                        Hovered::default(),
                        Action::Select(i(tile, "index")),
                        Tooltip(format!(
                            "{}{}",
                            s(tile, "name"),
                            if tile["known"] == false {
                                "  (locked)"
                            } else {
                                ""
                            }
                        )),
                        BackgroundColor(if selected {
                            SELECTED
                        } else if can {
                            TILE
                        } else {
                            UNAVAILABLE
                        }),
                        BorderColor::all(if can { GOOD } else { Color::NONE }),
                    ))
                    .with_children(|p| {
                        let path = s(tile, "icon");
                        if path.is_empty() {
                            label(
                                p,
                                s(tile, "name"),
                                Rect::new(
                                    6. * l.scale,
                                    6. * l.scale,
                                    l.tile - 6. * l.scale,
                                    l.tile - 6. * l.scale,
                                ),
                                18. * l.scale,
                                if tile["craftable"] == true {
                                    TEXT
                                } else {
                                    DISABLED_TEXT
                                },
                                &font.0,
                            );
                        } else {
                            icon(
                                p,
                                &path,
                                Rect::new(
                                    10. * l.scale,
                                    10. * l.scale,
                                    l.tile - 10. * l.scale,
                                    l.tile - 10. * l.scale,
                                ),
                                tile["craftable"] == true,
                                &assets,
                            );
                        }
                        if tile["known"] == false {
                            lock(
                                p,
                                Rect::new(
                                    l.tile * 0.32,
                                    l.tile * 0.285,
                                    l.tile * 0.68,
                                    l.tile * 0.715,
                                ),
                                &lock_image,
                            );
                        }
                    });
                }
                if ntiles == 0 {
                    label(
                        p,
                        if menu.options.is_null() {
                            "Loading recipes…"
                        } else {
                            "nothing here"
                        },
                        Rect::new(8., 8., l.grid.width() - 8., 50.),
                        23. * l.scale,
                        DIM,
                        &font.0,
                    );
                }
            });
            if !menu.browse.search.is_empty() {
                control(
                    p,
                    "×",
                    Rect::new(
                        l.search.max.x + 8.,
                        l.search.min.y,
                        l.search.max.x + 40.,
                        l.search.max.y,
                    ),
                    Action::Clear,
                    true,
                    false,
                    &font.0,
                    23. * l.scale,
                );
            }
            p.spawn((rect(l.queue), BackgroundColor(BAR_SOLID)))
                .with_children(|p| {
                    label(
                        p,
                        "CRAFTING QUEUE",
                        Rect::new(16. * l.scale, 10. * l.scale, 192. * l.scale, 40. * l.scale),
                        20. * l.scale,
                        DIM,
                        &font.0,
                    );
                    if nqueue == 0 {
                        label(
                            p,
                            "(empty)",
                            Rect::new(16. * l.scale, 48. * l.scale, 192. * l.scale, 74. * l.scale),
                            18. * l.scale,
                            DISABLED_TEXT,
                            &font.0,
                        );
                    }
                    p.spawn((
                        QueueArea,
                        Node {
                            overflow: Overflow::clip(),
                            ..rect(Rect::from_corners(
                                l.queue_row.min - l.queue.min,
                                l.queue_row.max - l.queue.min,
                            ))
                        },
                    ))
                    .with_children(|p| {
                        p.spawn((
                            QueueStrip,
                            at(0., 0., l.queue_row.width(), l.queue_row.height()),
                            UiTransform::default(),
                        ))
                        .with_children(|p| {
                            for (index, job) in menu.options["queue"]
                                .as_array()
                                .into_iter()
                                .flatten()
                                .enumerate()
                            {
                                // Owner wire oldest-first: active slot0 stays rightmost. Root
                                // input translates the strip; no entity is replaced during drag.
                                let size = 84. * l.scale;
                                let x = l.queue_row.width() - (index as f32 + 1.) * 92. * l.scale;
                                let y = (l.queue_row.height() - size) * 0.5;
                                p.spawn((
                                    at(x, y, size, size),
                                    Button,
                                    Hovered::default(),
                                    Action::Cancel(i(job, "slot") as u8),
                                    Tooltip(format!("{} · click to cancel", s(job, "name"))),
                                    BackgroundColor(if index == 0 { SELECTED } else { TILE }),
                                ))
                                .with_children(|p| {
                                    let path = s(job, "icon");
                                    if path.is_empty() {
                                        label(
                                            p,
                                            s(job, "name"),
                                            Rect::new(0., 0., size, size),
                                            13. * l.scale,
                                            TEXT,
                                            &font.0,
                                        );
                                    } else {
                                        icon(
                                            p,
                                            &path,
                                            Rect::new(
                                                7. * l.scale,
                                                7. * l.scale,
                                                size - 7. * l.scale,
                                                size - 9. * l.scale,
                                            ),
                                            true,
                                            &assets,
                                        );
                                    }
                                    if index == 0 {
                                        p.spawn((
                                            at(
                                                3. * l.scale,
                                                size - 7. * l.scale,
                                                size - 6. * l.scale,
                                                4. * l.scale,
                                            ),
                                            Pickable::IGNORE,
                                        ))
                                        .with_child((
                                            Progress {
                                                left: f(job, "left"),
                                                total: f(job, "total"),
                                                tick: menu.options["tick"].as_u64().unwrap_or(0),
                                            },
                                            Node {
                                                position_type: PositionType::Absolute,
                                                left: px(0),
                                                top: px(0),
                                                height: percent(100),
                                                width: percent(0),
                                                ..default()
                                            },
                                            BackgroundColor(GOOD),
                                            Pickable::IGNORE,
                                        ));
                                    }
                                });
                            }
                        });
                    });
                });
            draw_details(p, &view["selected"], l, &font.0, &assets);
            p.spawn((
                TooltipText,
                Text::new(""),
                TextFont {
                    font: font.0.clone().into(),
                    font_size: FontSize::Px(13.),
                    ..default()
                },
                TextColor(TEXT),
                Node {
                    position_type: PositionType::Absolute,
                    ..default()
                },
                BackgroundColor(Color::srgba(0., 0., 0., 0.9)),
                Pickable::IGNORE,
                Visibility::Hidden,
            ));
        })
        .id();
    commands.entity(root).add_child(field);
}
pub(super) fn tooltip(
    windows: Query<&Window, With<PrimaryWindow>>,
    source: Query<(&Tooltip, &Hovered)>,
    mut controls: Query<(
        Entity,
        &ControlStyle,
        Option<&Hovered>,
        Has<bevy::ui::Pressed>,
        &mut BackgroundColor,
    )>,
    hierarchy: Query<&Children>,
    mut colors: Query<&mut TextColor>,
    mut target: Query<(&mut Text, &mut Node, &mut Visibility), With<TooltipText>>,
) {
    for (entity, style, hover, pressed, mut background) in &mut controls {
        if style.field {
            continue;
        }
        let hover = hover.is_some_and(|h| h.get());
        background.0 = if !style.enabled {
            Color::srgba(0.1, 0.1, 0.1, 0.3)
        } else if pressed {
            Color::srgba(0., 0., 0., 0.6)
        } else if hover {
            Color::srgba(0.225, 0.225, 0.225, 0.6)
        } else if style.flat {
            Color::NONE
        } else {
            DEFAULT_PANEL
        };
        let ink = if !style.enabled {
            Color::srgba(0.875, 0.875, 0.875, 0.5)
        } else if pressed {
            Color::WHITE
        } else if hover {
            Color::srgb(0.95, 0.95, 0.95)
        } else {
            Color::srgb(0.875, 0.875, 0.875)
        };
        for child in hierarchy.iter_descendants(entity) {
            if let Ok(mut color) = colors.get_mut(child) {
                color.0 = ink;
            }
        }
    }
    let value = source
        .iter()
        .find(|(_, hover)| hover.get())
        .map(|(tip, _)| tip.0.as_str());
    let at = windows.single().ok().and_then(Window::cursor_position);
    for (mut text, mut node, mut visible) in &mut target {
        if let (Some(value), Some(at)) = (value, at) {
            **text = value.into();
            node.left = px(at.x + 14.);
            node.top = px(at.y + 16.);
            *visible = Visibility::Inherited;
        } else {
            *visible = Visibility::Hidden;
        }
    }
}
