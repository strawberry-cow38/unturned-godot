//! MenuNavbar.cs: the same four tabs, source theme and live bindings for every dashboard.
use super::*;
use bevy::picking::Pickable;

/// UITheme.Neutral; source authored surfaces retain their relative luminance.
pub(super) fn neutral(r: f32, g: f32, b: f32, alpha: f32) -> Color {
    let y = r * 0.2126 + g * 0.7152 + b * 0.0722;
    let value = y + (1. - y) * 0.12;
    Color::srgba(value, value, value, (alpha * 0.8).clamp(0., 1.))
}

#[derive(Component)]
struct Tab {
    active: bool,
    enabled: bool,
}

pub(super) struct NavbarPlugin;
impl Plugin for NavbarPlugin {
    fn build(&self, app: &mut App) {
        app.add_systems(Update, appearance.in_set(UiUpdate).after(rebuild_overlay));
    }
}

fn appearance(
    mut tabs: Query<(&Tab, &Hovered, &Children, &mut BackgroundColor)>,
    mut text: Query<&mut TextColor>,
) {
    for (tab, hover, children, mut color) in &mut tabs {
        color.0 = if tab.active {
            neutral(0.55, 0.62, 0.70, 0.72)
        } else if tab.enabled && hover.get() {
            neutral(0.40, 0.48, 0.56, 0.70)
        } else {
            Color::srgba(1., 1., 1., 0.03)
        };
        for child in children {
            if let Ok(mut color) = text.get_mut(*child) {
                color.0 = if !tab.enabled {
                    DIM
                } else if tab.active {
                    Color::WHITE
                } else if hover.get() {
                    TEXT
                } else {
                    Color::srgb(0.79, 0.79, 0.79)
                };
            }
        }
    }
}

pub(super) fn build(
    root: &mut ChildSpawnerCommands,
    width: f32,
    settings: &Settings,
    active: Screen,
    has_map: bool,
    font: &Handle<Font>,
) {
    root.spawn((
        Node {
            position_type: PositionType::Absolute,
            width: px(width),
            height: px(60),
            ..default()
        },
        BackgroundColor(neutral(0.13, 0.18, 0.24, 0.80)),
    ))
    .with_children(|p| {
        let w = (width - 24. - 24.) / 4.;
        for (i, (label, action, screen)) in [
            ("Inventory", GameAction::Inventory, Screen::Inventory),
            ("Craft", GameAction::Craft, Screen::Crafting),
            ("Skills", GameAction::Skills, Screen::Skills),
            ("Information", GameAction::Map, Screen::Information),
        ]
        .into_iter()
        .enumerate()
        {
            let is_active = screen == active;
            let enabled = screen != Screen::Information || has_map;
            let mut tab = p.spawn((
                Tab {
                    active: is_active,
                    enabled,
                },
                Node {
                    position_type: PositionType::Absolute,
                    left: px(12. + i as f32 * (w + 8.)),
                    top: px(8),
                    width: px(w),
                    height: px(44),
                    align_items: AlignItems::Center,
                    justify_content: JustifyContent::Center,
                    border_radius: BorderRadius::all(px(4)),
                    ..default()
                },
                BackgroundColor(if is_active {
                    neutral(0.55, 0.62, 0.70, 0.72)
                } else {
                    Color::srgba(1., 1., 1., 0.03)
                }),
                Hovered::default(),
            ));
            if enabled {
                tab.insert((Button, UiButton::Open(screen)));
            }
            tab.with_children(|p| {
                p.spawn((
                    Text::new(format!(
                        "{label} [{}]",
                        settings
                            .bindings
                            .get(&action)
                            .map(Binding::label)
                            .unwrap_or_default()
                    )),
                    TextFont {
                        font: font.clone().into(),
                        font_size: FontSize::Px(32.),
                        ..default()
                    },
                    TextColor(if !enabled {
                        DIM
                    } else if is_active {
                        Color::WHITE
                    } else {
                        Color::srgb(0.79, 0.79, 0.79)
                    }),
                    TextLayout::no_wrap(),
                    Pickable::IGNORE,
                ));
                if is_active {
                    p.spawn((
                        Node {
                            position_type: PositionType::Absolute,
                            bottom: px(0),
                            width: percent(100),
                            height: px(3),
                            ..default()
                        },
                        BackgroundColor(ACCENT),
                        Pickable::IGNORE,
                    ));
                }
            });
        }
        p.spawn((
            Node {
                position_type: PositionType::Absolute,
                top: px(59),
                width: percent(100),
                height: px(1),
                ..default()
            },
            BackgroundColor(Color::srgba(1., 1., 1., 0.1)),
            Pickable::IGNORE,
        ));
    });
}
