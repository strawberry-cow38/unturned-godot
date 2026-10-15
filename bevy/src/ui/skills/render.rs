use super::*;
use bevy::picking::Pickable;

#[derive(Component)]
pub(super) struct Root;
#[derive(Clone, PartialEq)]
pub(super) struct Signature {
    epoch: Option<u64>,
    map: String,
    tree: Tree,
    selected: Option<String>,
    pan: Vec2,
    viewport: Vec2,
    bindings: String,
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
fn at(position: Vec2, size: Vec2) -> Node {
    rect(Rect::from_corners(position, position + size))
}
fn label(
    p: &mut ChildSpawnerCommands,
    content: impl Into<String>,
    bounds: Rect,
    size: f32,
    color: Color,
    justify: Justify,
    font: &bevy::text::FontSource,
) {
    p.spawn((
        rect(bounds),
        Text::new(content),
        TextFont {
            font: font.clone(),
            font_size: FontSize::Px(size),
            ..default()
        },
        TextColor(color),
        TextLayout::new(justify, LineBreak::WordBoundary),
        Pickable::IGNORE,
    ));
}
fn line(p: &mut ChildSpawnerCommands, a: Vec2, b: Vec2, live: bool) {
    let width = if live { 2.5 } else { 1.5 };
    let min = a.min(b);
    let delta = (b - a).abs();
    p.spawn((
        at(min - Vec2::splat(width * 0.5), delta + Vec2::splat(width)),
        BackgroundColor(if live {
            ACCENT.with_alpha(0.85)
        } else {
            Color::srgba(1., 1., 1., 0.16)
        }),
        Pickable::IGNORE,
    ));
}
pub(super) fn appearance(
    mut buttons: Query<(
        &Action,
        &Hovered,
        Has<Pressed>,
        Has<Button>,
        &Children,
        &mut BackgroundColor,
    )>,
    mut texts: Query<&mut TextColor>,
) {
    for (action, hover, pressed, enabled, children, mut color) in &mut buttons {
        if !matches!(action, Action::Learn) {
            continue;
        }
        color.0 = if !enabled {
            super::super::navbar::neutral(0.30, 0.34, 0.40, 0.34)
        } else if hover.get() && !pressed {
            super::super::navbar::neutral(0.40, 0.48, 0.56, 0.70)
        } else {
            super::super::navbar::neutral(0.55, 0.62, 0.70, 0.72)
        };
        for child in children {
            if let Ok(mut color) = texts.get_mut(*child) {
                color.0 = if !enabled {
                    Color::srgb(0.40, 0.41, 0.45)
                } else if hover.get() || pressed {
                    Color::WHITE
                } else {
                    TEXT
                };
            }
        }
    }
}
fn detail(p: &mut ChildSpawnerCommands, menu: &Menu, bounds: Rect, font: &bevy::text::FontSource) {
    p.spawn((
        Node {
            border_radius: BorderRadius::all(px(4)),
            ..rect(bounds)
        },
        BackgroundColor(PANEL),
        Pickable::IGNORE,
    ));
    let node = menu
        .tree
        .nodes
        .iter()
        .find(|n| Some(&n.id) == menu.selected.as_ref());
    let (title, blurb, grants, requirements, why, button_text, enabled) = if let Some(n) = node {
        let grants = format!(
            "GRANTS\n{}",
            n.grants
                .iter()
                .map(|g| format!("  • {g}"))
                .collect::<Vec<_>>()
                .join("\n")
        );
        let requirements = format!(
            "REQUIRES\n{}",
            if n.requires.is_empty() {
                "  • nothing".into()
            } else {
                n.requires
                    .iter()
                    .map(|id| {
                        let required = menu.tree.nodes.iter().find(|n| n.id == *id);
                        format!(
                            "  {} {}",
                            if required.is_some_and(|n| n.learned) {
                                "✓"
                            } else {
                                "✗"
                            },
                            required.map_or(id.as_str(), |n| n.title.as_str())
                        )
                    })
                    .collect::<Vec<_>>()
                    .join("\n")
            }
        );
        (
            n.title.clone(),
            n.blurb.clone(),
            grants,
            requirements,
            if n.learned || n.can_take {
                String::new()
            } else {
                n.reason.clone()
            },
            if n.learned {
                "Learned".into()
            } else {
                format!("Learn  ·  {} XP", n.cost)
            },
            n.can_take,
        )
    } else {
        ("Select a node".into(), "Pillars run across the top. Advanced pillars sit below and open once the node that unlocks them is learned.".into(), String::new(), String::new(), String::new(), "Learn".into(), false)
    };
    let origin = bounds.min + Vec2::splat(14.);
    let width = bounds.width() - 28.;
    for (content, y, h, size, color) in [
        (title, 0., 24., 16., TEXT),
        (blurb, 30., 54., 13., Color::srgb(0.79, 0.79, 0.79)),
        (grants, 90., 96., 13., Color::srgb(0.79, 0.79, 0.79)),
        (requirements, 192., 60., 11., DIM),
        (why, 258., 36., 11., DIM),
    ] {
        label(
            p,
            content,
            Rect::from_corners(origin + Vec2::new(0., y), origin + Vec2::new(width, y + h)),
            size,
            color,
            Justify::Left,
            font,
        );
    }
    let color = if enabled {
        super::super::navbar::neutral(0.55, 0.62, 0.70, 0.72)
    } else {
        super::super::navbar::neutral(0.30, 0.34, 0.40, 0.34)
    };
    let mut button = p.spawn((
        Node {
            border_radius: BorderRadius::all(px(4)),
            align_items: AlignItems::Center,
            justify_content: JustifyContent::Center,
            ..at(origin + Vec2::new(0., 302.), Vec2::new(width, 34.))
        },
        BackgroundColor(color),
        Hovered::default(),
        Action::Learn,
    ));
    if enabled {
        button.insert(Button);
    }
    button.with_children(|p| {
        p.spawn((
            Text::new(button_text),
            TextFont {
                font: font.clone(),
                font_size: FontSize::Px(13.),
                ..default()
            },
            TextColor(if enabled {
                TEXT
            } else {
                Color::srgb(0.4, 0.41, 0.45)
            }),
            Pickable::IGNORE,
        ));
    });
}
pub(super) fn build(
    mut commands: Commands,
    menu: Res<Menu>,
    state: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    font: Res<crate::character::nameplate::OriginalFont>,
    symbols: Res<Symbols>,
    roots: Query<(Entity, Option<&swoop::Swoop>), With<Root>>,
    mut cached: Local<Option<Signature>>,
) {
    let active = state.screen == Screen::Skills && model.connected && model.alive;
    let signature = active.then(|| Signature {
        epoch: menu.epoch,
        map: model.map_name.clone(),
        tree: menu.tree.clone(),
        selected: menu.selected.clone(),
        pan: menu.pan,
        viewport: menu.viewport,
        bindings: format!("{:?}", settings.bindings),
    });
    if *cached == signature {
        return;
    }
    *cached = signature;
    for (entity, swoop) in &roots {
        swoop::replace::<Root>(&mut commands, entity, swoop, &state, &model);
    }
    if !active {
        return;
    }
    let layout = geometry::Layout::new(menu.viewport);
    let graph = geometry::Graph::new(&menu.tree);
    // Godot uses its system fallback for U+2713/U+2717. Keep the same measured
    // DejaVu outlines available without depending on the user's installed fonts.
    let graph_font = bevy::text::FontSource::list([font.0.clone(), symbols.0.clone()]);
    commands
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
            swoop::Swoop(Screen::Skills),
            UiTransform::default(),
        ))
        .with_children(|root| {
            root.spawn((
                Node {
                    border_radius: BorderRadius::all(px(6)),
                    ..rect(layout.panel)
                },
                BackgroundColor(super::super::navbar::neutral(0.10, 0.13, 0.18, 0.42)),
            ))
            .with_children(|p| {
                let size = layout.panel.size();
                let learned = menu.tree.nodes.iter().filter(|n| n.learned).count();
                let ready = menu.tree.nodes.iter().filter(|n| n.can_take).count();
                label(
                    p,
                    format!(
                        "SKILLS   ·   {learned} / {} learned   ·   {ready} available now",
                        menu.tree.nodes.len()
                    ),
                    Rect::from_corners(
                        Vec2::new(16., 68.),
                        Vec2::new((size.x - 216.).max(136.), 92.),
                    ),
                    13.,
                    DIM,
                    Justify::Left,
                    &graph_font,
                );
                label(
                    p,
                    format!("{} XP", menu.tree.experience),
                    Rect::from_corners(Vec2::new(size.x - 216., 64.), Vec2::new(size.x - 16., 90.)),
                    16.,
                    ACCENT,
                    Justify::Right,
                    &graph_font,
                );
                p.spawn((
                    Node {
                        overflow: Overflow::clip(),
                        ..rect(layout.clip)
                    },
                    BackgroundColor(Color::NONE),
                ))
                .with_children(|clip| {
                    clip.spawn((at(menu.pan, graph.size), Pickable::IGNORE))
                        .with_children(|canvas| {
                            for (a, b, live) in graph.edges(&menu.tree) {
                                let mid = (a.y + b.y) * 0.5;
                                line(canvas, a, Vec2::new(a.x, mid), live);
                                line(canvas, Vec2::new(a.x, mid), Vec2::new(b.x, mid), live);
                                line(canvas, Vec2::new(b.x, mid), b, live);
                            }
                            for pillar in &menu.tree.pillars {
                                let Some(at) = graph.heads.get(&pillar.id) else {
                                    continue;
                                };
                                let bounds = Rect::from_corners(
                                    *at,
                                    *at + Vec2::new(geometry::NODE.x, geometry::HEAD),
                                );
                                canvas.spawn((
                                    Node {
                                        border_radius: BorderRadius::all(px(4)),
                                        ..rect(bounds)
                                    },
                                    BackgroundColor(if pillar.open {
                                        Color::srgba(0.20, 0.26, 0.33, 0.98)
                                    } else {
                                        Color::srgba(0.15, 0.15, 0.17, 0.92)
                                    }),
                                    Pickable::IGNORE,
                                ));
                                label(
                                    canvas,
                                    &pillar.title,
                                    Rect::from_corners(*at + Vec2::new(0., 12.), bounds.max),
                                    16.,
                                    if pillar.open { TEXT } else { DIM },
                                    Justify::Center,
                                    &graph_font,
                                );
                            }
                            for node in &menu.tree.nodes {
                                let Some(at) = graph.nodes.get(&node.id) else {
                                    continue;
                                };
                                let color = if node.learned {
                                    Color::srgba(0.30, 0.42, 0.30, 0.98)
                                } else if node.can_take {
                                    Color::srgba(0.24, 0.28, 0.34, 0.98)
                                } else {
                                    Color::srgba(0.16, 0.16, 0.17, 0.94)
                                };
                                canvas.spawn((
                                    Node {
                                        border_radius: BorderRadius::all(px(4)),
                                        border: UiRect::all(px(if node.can_take {
                                            2.
                                        } else {
                                            0.
                                        })),
                                        ..self::at(*at, geometry::NODE)
                                    },
                                    BackgroundColor(color),
                                    BorderColor::all(if node.can_take {
                                        ACCENT
                                    } else {
                                        Color::NONE
                                    }),
                                    Hovered::default(),
                                    Button,
                                    Action::Select(node.id.clone()),
                                ));
                                label(
                                    canvas,
                                    &node.title,
                                    Rect::from_corners(
                                        *at + Vec2::new(0., 14.),
                                        *at + Vec2::new(geometry::NODE.x, 40.),
                                    ),
                                    16.,
                                    if node.learned || node.can_take {
                                        TEXT
                                    } else {
                                        DIM
                                    },
                                    Justify::Center,
                                    &graph_font,
                                );
                                label(
                                    canvas,
                                    if node.learned {
                                        "learned".into()
                                    } else {
                                        format!("{} XP", node.cost)
                                    },
                                    Rect::from_corners(
                                        *at + Vec2::new(0., 46.),
                                        *at + Vec2::new(geometry::NODE.x, 68.),
                                    ),
                                    13.,
                                    if node.can_take { ACCENT } else { DIM },
                                    Justify::Center,
                                    &graph_font,
                                );
                            }
                        });
                });
                detail(p, &menu, layout.detail, &graph_font);
            });
            // Reuse the original four-tab navbar rather than inventing a fifth menu shell.
            super::super::navbar::build(
                root,
                menu.viewport.x,
                &settings,
                Screen::Skills,
                map::available(&model.map_name),
                &font.0,
            );
        });
}
