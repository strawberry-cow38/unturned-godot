use super::*;
use bevy::picking::Pickable;

#[derive(Component)]
pub(super) struct Root;
#[derive(Component)]
pub(super) struct Overview;
#[derive(Component)]
pub(super) struct Tile(usize);
#[derive(Component)]
pub(super) struct Arrow;
#[derive(Component)]
pub(super) struct Dot {
    norm: Vec2,
    peer: Option<u16>,
}
#[derive(Component)]
pub(super) struct Tag {
    norm: Vec2,
    pip: f32,
    priority: (u8, usize),
    peer: Option<u16>,
}
#[derive(Component)]
pub(super) struct Pin(Vec2);
#[derive(Component)]
pub(super) struct Coordinate;
#[derive(Component)]
pub(super) struct RosterCell {
    id: u16,
    distance: bool,
}
#[derive(Component)]
pub(super) struct RosterScroll;

pub(super) fn rect(rect: Rect) -> Node {
    Node {
        position_type: PositionType::Absolute,
        left: px(rect.min.x),
        top: px(rect.min.y),
        width: px(rect.width()),
        height: px(rect.height()),
        ..default()
    }
}
fn at(at: Vec2, size: Vec2) -> Node {
    rect(Rect::from_corners(at, at + size))
}
fn panel(
    p: &mut ChildSpawnerCommands,
    bounds: Rect,
    build: impl FnOnce(&mut ChildSpawnerCommands),
) {
    p.spawn((
        Node {
            padding: UiRect::all(px(10)),
            border_radius: BorderRadius::all(px(6)),
            ..rect(bounds)
        },
        BackgroundColor(PANEL),
    ))
    .with_children(build);
}
fn tag(
    p: &mut ChildSpawnerCommands,
    label: &str,
    norm: Vec2,
    pip: f32,
    priority: (u8, usize),
    peer: Option<u16>,
    color: Color,
) {
    p.spawn((
        Tag {
            norm,
            pip,
            priority,
            peer,
        },
        Node {
            position_type: PositionType::Absolute,
            padding: UiRect::axes(px(7), px(3)),
            border_radius: BorderRadius::all(px(5)),
            ..default()
        },
        BackgroundColor(Color::srgba(0., 0., 0., 0.55)),
        Pickable::IGNORE,
    ))
    .with_children(|p| {
        p.spawn((text(label, 18., color), TextLayout::no_wrap()));
    });
}
fn pip(p: &mut ChildSpawnerCommands, norm: Vec2, size: f32, color: Color, peer: Option<u16>) {
    p.spawn((
        Dot { norm, peer },
        Node {
            border: UiRect::all(px(3)),
            border_radius: BorderRadius::MAX,
            ..at(Vec2::ZERO, Vec2::splat(size))
        },
        BackgroundColor(color),
        BorderColor::all(Color::srgba(0., 0., 0., 0.85)),
        Pickable::IGNORE,
    ));
}
fn navbar(p: &mut ChildSpawnerCommands, width: f32, settings: &Settings) {
    p.spawn((
        at(Vec2::ZERO, Vec2::new(width, 60.)),
        BackgroundColor(Color::srgba(0.16, 0.16, 0.16, 0.96)),
    ))
    .with_children(|p| {
        let w = (width - 24. - 24.) / 4.;
        for (i, (label, action, target)) in [
            ("Inventory", GameAction::Inventory, Some(Screen::Inventory)),
            ("Craft", GameAction::Craft, Some(Screen::Crafting)),
            ("Skills", GameAction::Skills, None),
            ("Information", GameAction::Map, Some(Screen::Information)),
        ]
        .into_iter()
        .enumerate()
        {
            let active = target == Some(Screen::Information);
            let bg = if active {
                Color::srgba(0.30, 0.31, 0.33, 0.96)
            } else {
                Color::srgba(1., 1., 1., 0.03)
            };
            let mut tab = p.spawn((
                Node {
                    align_items: AlignItems::Center,
                    justify_content: JustifyContent::Center,
                    border_radius: BorderRadius::all(px(4)),
                    ..at(Vec2::new(12. + i as f32 * (w + 8.), 8.), Vec2::new(w, 44.))
                },
                BackgroundColor(bg),
                ButtonBaseColor(bg),
                Hovered::default(),
            ));
            if let Some(screen) = target {
                tab.insert((Button, UiButton::Open(screen)));
            }
            tab.with_children(|p| {
                p.spawn(text(
                    format!(
                        "{label} [{}]",
                        settings
                            .bindings
                            .get(&action)
                            .map(Binding::label)
                            .unwrap_or_default()
                    ),
                    32.,
                    if target.is_some() { TEXT } else { DIM },
                ));
                if active {
                    p.spawn((
                        Node {
                            position_type: PositionType::Absolute,
                            bottom: px(0),
                            width: percent(100),
                            height: px(3),
                            ..default()
                        },
                        BackgroundColor(ACCENT),
                    ));
                }
            });
        }
    });
}
pub(super) fn build(
    mut commands: Commands,
    roots: Query<Entity, With<Root>>,
    state: Res<UiState>,
    model: Res<UiModel>,
    map: Res<Dashboard>,
    art: Res<MapAssets>,
    quests: Res<npc::Menu>,
    settings: Res<Settings>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut cached: Local<String>,
) {
    let Some(layout) = map.layout else {
        cached.clear();
        for entity in &roots {
            commands.entity(entity).despawn();
        }
        return;
    };
    let signature = format!(
        "{:?}|{}|{}|{:?}|{:?}|{:?}|{}|{:?}",
        state.screen,
        model.connected,
        art.key,
        layout,
        map.view.markers,
        map.roster
            .iter()
            .map(|p| (p.id, &p.name))
            .collect::<Vec<_>>(),
        quests.quest_signature(),
        settings.bindings
    );
    if *cached == signature {
        return;
    }
    *cached = signature;
    for entity in &roots {
        commands.entity(entity).despawn();
    }
    if state.screen != Screen::Information || !model.connected {
        return;
    }
    let Ok(window) = windows.single() else {
        return;
    };
    let vp = Vec2::new(window.width(), window.height());
    commands
        .spawn((
            Root,
            Node {
                width: percent(100),
                height: percent(100),
                position_type: PositionType::Absolute,
                ..default()
            },
            GlobalZIndex(20),
            BackgroundColor(Color::srgba(0.03, 0.04, 0.04, 0.8)),
        ))
        .with_children(|p| {
            p.spawn((
                Node {
                    border_radius: BorderRadius::all(px(6)),
                    ..at(Vec2::splat(16.), vp - Vec2::splat(32.))
                },
                BackgroundColor(Color::srgba(0.22, 0.22, 0.23, 0.9)),
            ));
            navbar(p, vp.x, &settings);
            p.spawn((
                at(Vec2::new(16., 80.), Vec2::new(vp.x - 32., 30.)),
                BackgroundColor(PANEL),
            ))
            .with_children(|p| {
                p.spawn((
                    Coordinate,
                    text("", 16., TEXT),
                    Node {
                        left: px(12),
                        top: px(4),
                        position_type: PositionType::Absolute,
                        ..default()
                    },
                ));
            });
            panel(p, layout.quests, |p| {
                npc::quest_panel(p, &quests, layout.quests.height() - 20., false);
            });
            panel(p, layout.roster, |p| {
                p.spawn((
                    text(format!("PLAYERS ({})", map.roster.len()), 18., TEXT),
                    at(
                        Vec2::new(8., 0.),
                        Vec2::new(layout.roster.width() - 16., 30.),
                    ),
                ));
                let bottom = super::super::vitals::content_bottom(
                    vp,
                    layout.roster.min.x,
                    layout.roster.max.x,
                    layout.roster.max.y,
                );
                p.spawn((
                    RosterScroll,
                    ScrollPosition(Vec2::new(0., map.roster_scroll)),
                    Node {
                        overflow: Overflow::scroll_y(),
                        flex_direction: FlexDirection::Column,
                        row_gap: px(6),
                        ..at(
                            Vec2::new(0., 30.),
                            Vec2::new(
                                layout.roster.width() - 20.,
                                (bottom - layout.roster.min.y - 48.).max(30.),
                            ),
                        )
                    },
                ))
                .with_children(|p| {
                    for person in &map.roster {
                        p.spawn((
                            Node {
                                height: px(30),
                                min_height: px(30),
                                column_gap: px(12),
                                align_items: AlignItems::Center,
                                ..default()
                            },
                            BackgroundColor(Color::srgba(0.22, 0.22, 0.23, 0.98)),
                        ))
                        .with_children(|p| {
                            p.spawn((
                                text(
                                    if person.me {
                                        format!("{} (you)", person.name)
                                    } else {
                                        person.name.clone()
                                    },
                                    16.,
                                    if person.me {
                                        Color::srgb(0.25, 0.9, 1.)
                                    } else {
                                        TEXT
                                    },
                                ),
                                Node {
                                    width: px(220),
                                    flex_shrink: 0.,
                                    ..default()
                                },
                                TextLayout::no_wrap(),
                            ));
                            p.spawn((
                                RosterCell {
                                    id: person.id,
                                    distance: false,
                                },
                                text("", 16., DIM),
                                Node {
                                    width: px(190),
                                    flex_shrink: 0.,
                                    ..default()
                                },
                                TextLayout::no_wrap(),
                            ));
                            p.spawn((
                                RosterCell {
                                    id: person.id,
                                    distance: true,
                                },
                                text("", 16., DIM),
                                Node {
                                    width: px(100),
                                    flex_shrink: 0.,
                                    ..default()
                                },
                                TextLayout::no_wrap(),
                            ));
                        });
                    }
                });
            });
            p.spawn((
                Node {
                    overflow: Overflow::clip(),
                    ..rect(layout.map)
                },
                BackgroundColor(Color::BLACK),
            ))
            .with_children(|p| {
                if let Some(image) = &art.overview {
                    p.spawn((
                        Overview,
                        ImageNode::new(image.clone()),
                        at(Vec2::ZERO, Vec2::splat(layout.map.width())),
                        Pickable::IGNORE,
                    ));
                } else {
                    p.spawn(text(&art.error, 18., TEXT));
                }
                for i in 0..16 {
                    p.spawn((
                        Tile(i),
                        ImageNode::default(),
                        at(Vec2::ZERO, Vec2::ZERO),
                        Visibility::Hidden,
                        Pickable::IGNORE,
                    ));
                }
                for (i, (name, norm)) in art.towns.iter().enumerate() {
                    pip(p, *norm, 15., ACCENT, None);
                    tag(p, name, *norm, 15., (2, i), None, Color::WHITE);
                }
                for (i, person) in map.roster.iter().filter(|p| !p.me).enumerate() {
                    let norm = geometry::normalized(person.position);
                    pip(p, norm, 18., Color::srgb(1., 0.55, 0.2), Some(person.id));
                    tag(
                        p,
                        &person.name,
                        norm,
                        18.,
                        (1, i),
                        Some(person.id),
                        Color::srgb(1., 0.78, 0.55),
                    );
                }
                for marker in &map.view.markers {
                    p.spawn((
                        Pin(marker.norm),
                        ImageNode::new(art.pin.clone()),
                        at(Vec2::ZERO, Vec2::splat(64.)),
                        Pickable::IGNORE,
                    ));
                    tag(
                        p,
                        &format!("M{}", marker.serial),
                        marker.norm,
                        20.,
                        (0, marker.serial as usize),
                        None,
                        Color::srgb(1., 0.85, 0.2),
                    );
                }
                p.spawn((
                    Arrow,
                    ImageNode::new(art.arrow.clone()),
                    at(Vec2::ZERO, Vec2::splat(64.)),
                    UiTransform::default(),
                    Pickable::IGNORE,
                ));
            });
        });
}

pub(super) fn place(
    map: Res<Dashboard>,
    art: Res<MapAssets>,
    state: Res<UiState>,
    settings: Res<Settings>,
    mut nodes: Query<(
        Entity,
        &mut Node,
        Option<&Overview>,
        Option<&Tile>,
        Option<&Arrow>,
        Option<&Dot>,
        Option<&Tag>,
        Option<&Pin>,
        Option<&ComputedNode>,
        Option<&mut ImageNode>,
        Option<&mut Visibility>,
        Option<&mut UiTransform>,
    )>,
    mut labels: Query<(&mut Text, Option<&Coordinate>, Option<&RosterCell>)>,
    mut scroll: Query<(&mut ScrollPosition, &ComputedNode), With<RosterScroll>>,
    images: Res<Assets<Image>>,
    mut diagnostics: ResMut<MapDiagnostics>,
) {
    if state.screen != Screen::Information {
        diagnostics.0 = json!({"open":false});
        return;
    }
    let Some(layout) = map.layout else {
        return;
    };
    let side = layout.map.width();
    let size = side * map.view.zoom;
    let pan = map.view.pan;
    for (_, mut node, overview, tile, arrow, dot, tag, pin, _, image, visible, transform) in
        &mut nodes
    {
        if tag.is_some() {
            continue;
        }
        let positioned = if overview.is_some() {
            node.width = px(size);
            node.height = px(size);
            Some(pan)
        } else if let Some(tile) = tile {
            let t = size / 4.;
            node.width = px(t);
            node.height = px(t);
            if let (Some(mut image), Some(mut visible)) = (image, visible) {
                if let Some(handle) = &art.tiles[tile.0] {
                    image.image = handle.clone();
                    *visible = Visibility::Inherited;
                } else {
                    image.image = Handle::default();
                    *visible = Visibility::Hidden;
                }
            }
            Some(pan + Vec2::new((tile.0 % 4) as f32, (tile.0 / 4) as f32) * t)
        } else if arrow.is_some() {
            if let Some(mut transform) = transform {
                transform.rotation = Rot2::radians(map.angle);
            }
            Some(pan + geometry::normalized(map.position) * size - Vec2::splat(32.))
        } else if let Some(dot) = dot {
            let norm = dot
                .peer
                .and_then(|id| map.roster.iter().find(|p| p.id == id))
                .map(|p| geometry::normalized(p.position))
                .unwrap_or(dot.norm);
            Some(pan + norm * size - Vec2::splat(if dot.peer.is_some() { 9. } else { 7.5 }))
        } else {
            pin.map(|p| pan + p.0 * size - Vec2::splat(32.))
        };
        if let Some(at) = positioned {
            node.left = px(at.x);
            node.top = px(at.y);
        }
    }
    let mut tags = nodes
        .iter()
        .filter_map(|(entity, _, _, _, _, _, tag, _, _, _, _, _)| tag.map(|t| (t.priority, entity)))
        .collect::<Vec<_>>();
    tags.sort_by_key(|(priority, _)| *priority);
    let mut placed = vec![];
    for (_, entity) in tags {
        let Ok((_, mut node, _, _, _, _, Some(tag), _, Some(computed), _, _, _)) =
            nodes.get_mut(entity)
        else {
            continue;
        };
        let norm = tag
            .peer
            .and_then(|id| map.roster.iter().find(|p| p.id == id))
            .map(|p| geometry::normalized(p.position))
            .unwrap_or(tag.norm);
        let anchor = pan
            + norm * size
            + if tag.priority.0 == 0 {
                Vec2::new(0., -15.)
            } else {
                Vec2::ZERO
            };
        let measured = computed.size() * computed.inverse_scale_factor();
        let position = geometry::label(
            anchor,
            measured,
            tag.pip,
            Rect::from_corners(Vec2::ZERO, Vec2::splat(side)),
            &mut placed,
        );
        // Display::None would remove text measurement and make labels oscillate.
        // Move rejected labels outside the clipped map, retaining their layout size.
        let at = position
            .filter(|_| measured.min_element() > 0.)
            .unwrap_or(Vec2::splat(-100000.));
        node.left = px(at.x);
        node.top = px(at.y);
    }
    diagnostics.0 = json!({
        "open": true, "map":map.map,"zoom":map.view.zoom,"pan":map.view.pan.to_array(),
        "clip":[layout.map.min.to_array(),layout.map.max.to_array()],"facingRadians":map.angle,
        "markers":map.view.markers.iter().map(|m|json!({"serial":m.serial,"norm":m.norm.to_array()})).collect::<Vec<_>>(),
        "labelsShown":placed.len(),
        "residentTiles":art.tiles.iter().flatten().count(),
        "loadedTiles":art.tiles.iter().flatten().filter(|h|images.contains(h.id())).count(),
        "players":map.roster.iter().map(|p|json!({"id":p.id,"name":p.name,"position":p.position.to_array(),"me":p.me})).collect::<Vec<_>>()
    });
    for (mut text, coordinate, cell) in &mut labels {
        if coordinate.is_some() {
            text.0 = format!(
                "{} | X {:.0} Z {:.0} | {:.2}x | LMB drag | Wheel zoom | RMB marker | [{}] / Esc close",
                map.map,
                map.position.x,
                map.position.z,
                map.view.zoom,
                settings
                    .bindings
                    .get(&GameAction::Map)
                    .map(Binding::label)
                    .unwrap_or_default()
            );
        } else if let Some(cell) = cell {
            if let Some(person) = map.roster.iter().find(|p| p.id == cell.id) {
                text.0 = if cell.distance {
                    if person.me {
                        "-".into()
                    } else {
                        let d = map.position.distance(person.position);
                        if d >= 1000. {
                            format!("{:.1} km", d / 1000.)
                        } else {
                            format!("{d:.0} m")
                        }
                    }
                } else {
                    format!("X {:.0}  Z {:.0}", person.position.x, person.position.z)
                };
            }
        }
    }
    for (mut scroll, node) in &mut scroll {
        scroll.y = map
            .roster_scroll
            .min(((node.content_size().y - node.size().y) * node.inverse_scale_factor()).max(0.));
    }
}

/// Reuse the original embedded font for the whole dashboard, including its
/// shared quest subtree, without changing unrelated menus.
pub(super) fn font(
    font: Res<crate::character::nameplate::OriginalFont>,
    roots: Query<(), With<Root>>,
    parents: Query<&ChildOf>,
    mut texts: Query<(Entity, &mut TextFont), Added<TextFont>>,
) {
    for (entity, mut text) in &mut texts {
        let mut current = entity;
        while let Ok(parent) = parents.get(current) {
            current = parent.parent();
            if roots.contains(current) {
                text.font = font.0.clone().into();
                break;
            }
        }
    }
}
