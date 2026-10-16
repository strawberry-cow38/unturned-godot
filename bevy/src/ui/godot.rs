//! Godot 4.6 default-theme controls and the project's canvas stretch, shared by
//! the ported main menu and settings screens. Sizes are authored in the source's
//! 2560x1440 base (project.godot `canvas_items`/`expand`) and multiplied by `k`.
use super::*;
use crate::character::nameplate::OriginalFont;
use bevy::window::PrimaryWindow;

/// Godot `canvas_items` stretch with `expand`: the smaller axis ratio.
pub(crate) fn stretch(window: Vec2) -> f32 {
    if window.x <= 0. || window.y <= 0. {
        return 0.5;
    }
    (window.x / 2560.).min(window.y / 1440.)
}
pub(crate) fn window_stretch(windows: &Query<&Window, With<PrimaryWindow>>) -> f32 {
    windows
        .single()
        .map_or(0.5, |w| stretch(Vec2::new(w.width(), w.height())))
}

// default_theme.cpp colours.
pub(crate) const NORMAL: Color = Color::srgba(0.1, 0.1, 0.1, 0.6);
pub(crate) const HOVER: Color = Color::srgba(0.225, 0.225, 0.225, 0.6);
pub(crate) const PRESSED: Color = Color::srgba(0., 0., 0., 0.6);
pub(crate) const DISABLED: Color = Color::srgba(0.1, 0.1, 0.1, 0.3);
pub(crate) const FONT: Color = Color::srgb(0.875, 0.875, 0.875);
pub(crate) const FONT_HOVER: Color = Color::srgb(0.95, 0.95, 0.95);
pub(crate) const FONT_DISABLED: Color = Color::srgba(0.875, 0.875, 0.875, 0.5);
/// MainMenu.Header / the generated-entry gold.
pub(crate) const GOLD: Color = Color::srgb(0.92, 0.86, 0.62);

/// UITheme colours (game/UITheme.cs).
pub(crate) mod ui_theme {
    use super::*;
    pub fn neutral(r: f32, g: f32, b: f32, a: f32) -> Color {
        super::super::navbar::neutral(r, g, b, a)
    }
    pub fn bar() -> Color {
        neutral(0.17, 0.24, 0.32, 0.78)
    }
    pub fn hover() -> Color {
        neutral(0.40, 0.48, 0.56, 0.70)
    }
    pub fn selected() -> Color {
        neutral(0.55, 0.62, 0.70, 0.72)
    }
    pub fn slot_empty() -> Color {
        neutral(0.30, 0.34, 0.40, 0.34)
    }
    pub const BG_SOLID: Color = Color::srgba(0.13, 0.13, 0.14, 0.96);
    pub const TEXT: Color = Color::srgb(0.88, 0.88, 0.91);
    pub const TEXT_DIM: Color = Color::srgb(0.55, 0.56, 0.60);
    pub const TEXT_DISABLED: Color = Color::srgb(0.40, 0.41, 0.45);
    pub const ACCENT: Color = Color::srgb(0.62, 0.78, 0.94);
    pub const GOOD: Color = Color::srgb(0.62, 0.82, 0.60);
    pub const BAD: Color = Color::srgb(0.86, 0.52, 0.46);
    pub const SCRIM: Color = Color::srgba(0., 0., 0., 0.72);
}

/// Which stylebox set a button wears.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Skin {
    /// Godot default Button.
    Default,
    /// UITheme.Button (secondary face).
    Theme,
    /// UITheme.Button(primary: true).
    Primary,
    /// Flat (Advanced close glyph).
    Flat,
}

/// Runtime appearance of a ported button: hover/pressed/toggle/disabled.
#[derive(Component, Clone, Debug)]
pub(crate) struct Look {
    pub skin: Skin,
    pub toggled: bool,
    pub disabled: bool,
    /// Text colour override (normal state), e.g. the gold generated entries.
    pub font: Option<Color>,
}

/// DejaVu Sans glyphs Godot's TextServer falls back to for the menu symbols
/// Open Sans lacks (see assets/fonts/README.md).
#[derive(Resource)]
pub(crate) struct MenuSymbols(pub Handle<Font>);
impl FromWorld for MenuSymbols {
    fn from_world(world: &mut World) -> Self {
        Self(world.resource_mut::<Assets<Font>>().add(Font::from_bytes(
            include_bytes!("../../assets/fonts/DejaVuSans_MenuSymbols.ttf").to_vec(),
        )))
    }
}
pub(crate) fn init_fonts(app: &mut App) {
    if app.world().contains_resource::<Assets<Font>>() {
        app.init_resource::<OriginalFont>().init_resource::<MenuSymbols>();
    }
}

#[derive(Clone)]
pub(crate) struct Ctx {
    pub k: f32,
    pub font: bevy::text::FontSource,
}
impl Ctx {
    pub fn px(&self, v: f32) -> Val {
        Val::Px(v * self.k)
    }
    pub fn text(&self, value: impl Into<String>, size: f32, color: Color) -> impl Bundle {
        (
            Text::new(value.into()),
            TextFont {
                font: self.font.clone(),
                font_size: FontSize::Px((size * self.k).max(1.)),
                ..default()
            },
            TextColor(color),
        )
    }
    /// A label whose box has a fixed width and wraps (AutowrapMode.WordSmart).
    pub fn wrapped(&self, value: impl Into<String>, size: f32, color: Color, width: f32) -> impl Bundle {
        (
            self.text(value, size, color),
            Node {
                width: self.px(width),
                ..default()
            },
        )
    }
    pub fn icon(&self, assets: &AssetServer, path: &str, size: f32) -> impl Bundle {
        (
            ImageNode::new(assets.load(path.to_owned())),
            Node {
                width: self.px(size),
                height: self.px(size),
                flex_shrink: 0.,
                ..default()
            },
            Pickable::IGNORE,
        )
    }
}

pub(crate) fn ctx(k: f32, font: Option<&OriginalFont>, symbols: Option<&MenuSymbols>) -> Ctx {
    let primary = font.map(|f| f.0.clone()).unwrap_or_default();
    Ctx {
        k,
        font: match symbols {
            Some(symbols) => bevy::text::FontSource::list([primary, symbols.0.clone()]),
            None => primary.into(),
        },
    }
}

/// Godot default PanelContainer stylebox (no content margin).
pub(crate) fn panel_color() -> BackgroundColor {
    BackgroundColor(NORMAL)
}

fn faces(skin: Skin) -> (Color, Color, Color, Color) {
    match skin {
        Skin::Default => (NORMAL, HOVER, PRESSED, DISABLED),
        Skin::Theme => (
            ui_theme::bar(),
            ui_theme::hover(),
            ui_theme::selected(),
            ui_theme::slot_empty(),
        ),
        Skin::Primary => (
            ui_theme::selected(),
            ui_theme::hover(),
            ui_theme::selected(),
            ui_theme::slot_empty(),
        ),
        Skin::Flat => (Color::NONE, Color::NONE, Color::NONE, Color::NONE),
    }
}
fn font_colors(skin: Skin) -> (Color, Color, Color) {
    match skin {
        Skin::Default | Skin::Flat => (FONT, FONT_HOVER, FONT_DISABLED),
        _ => (ui_theme::TEXT, Color::WHITE, ui_theme::TEXT_DISABLED),
    }
}

/// Spawn a button: `node` supplies size/layout, `action` the activation marker.
pub(crate) fn button<A: Component>(
    parent: &mut ChildSpawnerCommands,
    c: &Ctx,
    look: Look,
    node: Node,
    action: Option<A>,
    children: impl FnOnce(&mut ChildSpawnerCommands, Color),
) {
    let (normal, _, pressed, disabled) = faces(look.skin);
    let face = if look.disabled {
        disabled
    } else if look.toggled {
        pressed
    } else {
        normal
    };
    let (font, _, font_disabled) = font_colors(look.skin);
    let text = if look.disabled {
        font_disabled
    } else {
        look.font.unwrap_or(if look.toggled { Color::WHITE } else { font })
    };
    let radius = match look.skin {
        Skin::Default | Skin::Flat => 3.,
        _ => 4.,
    };
    let mut entity = parent.spawn((
        Node {
            border_radius: BorderRadius::all(c.px(radius)),
            ..node
        },
        BackgroundColor(face),
        Hovered::default(),
        look.clone(),
    ));
    if !look.disabled {
        if let Some(action) = action {
            entity.insert((Button, action));
        }
    }
    entity.with_children(|p| children(p, text));
}

/// A centred single-label button.
pub(crate) fn label_button<A: Component>(
    parent: &mut ChildSpawnerCommands,
    c: &Ctx,
    look: Look,
    mut node: Node,
    label: impl Into<String>,
    size: f32,
    action: Option<A>,
) {
    node.justify_content = JustifyContent::Center;
    node.align_items = AlignItems::Center;
    let label = label.into();
    button(parent, c, look, node, action, |p, color| {
        p.spawn((c.text(label, size, color), Pickable::IGNORE));
    });
}

pub(crate) fn look(skin: Skin) -> Look {
    Look {
        skin,
        toggled: false,
        disabled: false,
        font: None,
    }
}

/// Hover/pressed state for every ported button, including its label colour.
pub(crate) fn appearance(
    mut buttons: Query<(&Look, &Hovered, Has<bevy::ui::Pressed>, &Children, &mut BackgroundColor)>,
    mut labels: Query<&mut TextColor>,
) {
    for (look, hovered, pressed, children, mut background) in &mut buttons {
        let (normal, hover, down, disabled) = faces(look.skin);
        let (font, font_hover, font_disabled) = font_colors(look.skin);
        let (face, text) = if look.disabled {
            (disabled, font_disabled)
        } else if pressed || look.toggled {
            (down, look.font.unwrap_or(Color::WHITE))
        } else if hovered.get() {
            (hover, look.font.map_or(font_hover, |c| c.lighter(0.04)))
        } else {
            (normal, look.font.unwrap_or(font))
        };
        if background.0 != face {
            background.0 = face;
        }
        for child in children {
            if let Ok(mut color) = labels.get_mut(*child) {
                if color.0 != text {
                    color.0 = text;
                }
            }
        }
    }
}

/// HSeparator: 1px line in the default theme's separator grey, 4px separation.
pub(crate) fn separator(parent: &mut ChildSpawnerCommands, c: &Ctx) {
    parent.spawn((
        Node {
            height: c.px(4.),
            width: percent(100),
            align_items: AlignItems::Center,
            ..default()
        },
        Pickable::IGNORE,
    ))
    .with_children(|p| {
        p.spawn((
            Node {
                width: percent(100),
                height: px(c.k.max(1.)),
                ..default()
            },
            BackgroundColor(Color::srgba(0.5, 0.5, 0.5, 1.)),
        ));
    });
}

/// A ScrollContainer: clipped, wheel-scrolled by bevy_ui_widgets' ScrollArea.
pub(crate) fn scroll(width: Val, height: Val) -> impl Bundle {
    (
        Node {
            width,
            height,
            flex_direction: FlexDirection::Column,
            overflow: Overflow::scroll_y(),
            ..default()
        },
        bevy::ui_widgets::ScrollArea,
    )
}

/// ScrollContainer with its VScrollBar, shown only while content overflows.
pub(crate) fn scroll_box(
    parent: &mut ChildSpawnerCommands,
    c: &Ctx,
    key: &'static str,
    width: Val,
    height: Val,
    content: impl FnOnce(&mut ChildSpawnerCommands),
) {
    parent
        .spawn(Node {
            width,
            height,
            flex_direction: FlexDirection::Row,
            ..default()
        })
        .with_children(|frame| {
            let area = frame
                .spawn(scroll(Val::Auto, percent(100)))
                .insert(Node {
                    flex_grow: 1.,
                    height: percent(100),
                    flex_direction: FlexDirection::Column,
                    overflow: Overflow::scroll_y(),
                    ..default()
                })
                .insert(ScrollKey(key))
                .with_children(content)
                .id();
            frame
                .spawn((
                    ScrollTrack(area),
                    bevy::ui_widgets::Scrollbar::new(
                        area,
                        bevy::ui_widgets::ControlOrientation::Vertical,
                        (8. * c.k).max(4.),
                    ),
                    Node {
                        width: c.px(12.),
                        height: percent(100),
                        ..default()
                    },
                    BackgroundColor(Color::srgba(0.1, 0.1, 0.1, 0.6)),
                    Visibility::Hidden,
                ))
                .with_children(|bar| {
                    bar.spawn((
                        bevy::ui_widgets::ScrollbarThumb {
                            border_radius: BorderRadius::all(c.px(4.)),
                            ..default()
                        },
                        BackgroundColor(Color::srgba(1., 1., 1., 0.3)),
                    ));
                });
        });
}
#[derive(Component)]
pub(crate) struct ScrollTrack(Entity);
/// Godot builds these screens once, so a list keeps its scroll while rows
/// change. The native overlay is rebuilt per change; the offset survives it.
#[derive(Component, Clone, Copy)]
pub(crate) struct ScrollKey(pub &'static str);
#[derive(Resource, Default)]
pub(crate) struct ScrollMemory(pub std::collections::HashMap<&'static str, f32>);
pub(crate) fn restore_scroll(
    add: On<Add, ScrollKey>,
    keys: Query<&ScrollKey>,
    memory: Res<ScrollMemory>,
    mut commands: Commands,
) {
    if let Ok(key) = keys.get(add.entity) {
        if let Some(y) = memory.0.get(key.0) {
            commands.entity(add.entity).insert(ScrollPosition(Vec2::new(0., *y)));
        }
    }
}
pub(crate) fn remember_scroll(
    areas: Query<(&ScrollKey, &ScrollPosition), Changed<ScrollPosition>>,
    mut memory: ResMut<ScrollMemory>,
) {
    for (key, position) in &areas {
        memory.0.insert(key.0, position.y);
    }
}
/// Godot hides a ScrollContainer's bar until the content exceeds the page.
pub(crate) fn scroll_visibility(
    mut tracks: Query<(&ScrollTrack, &mut Visibility)>,
    areas: Query<&ComputedNode>,
) {
    for (track, mut visibility) in &mut tracks {
        let Ok(node) = areas.get(track.0) else { continue };
        let wanted = if node.content_size().y > node.size().y + 0.5 {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if *visibility != wanted {
            *visibility = wanted;
        }
    }
}

/// Godot CheckButton (switch) / CheckBox glyph, drawn instead of the theme icon.
pub(crate) fn switch(parent: &mut ChildSpawnerCommands, c: &Ctx, on: bool) {
    parent
        .spawn((
            Node {
                width: c.px(38.),
                height: c.px(20.),
                border_radius: BorderRadius::all(c.px(10.)),
                padding: UiRect::all(c.px(2.)),
                justify_content: if on {
                    JustifyContent::FlexEnd
                } else {
                    JustifyContent::FlexStart
                },
                ..default()
            },
            BackgroundColor(if on {
                Color::srgba(0.70, 0.70, 0.70, 0.9)
            } else {
                Color::srgba(0.30, 0.30, 0.30, 0.9)
            }),
            Pickable::IGNORE,
        ))
        .with_children(|p| {
            p.spawn((
                Node {
                    width: c.px(16.),
                    height: c.px(16.),
                    border_radius: BorderRadius::all(c.px(8.)),
                    ..default()
                },
                BackgroundColor(Color::srgb(0.95, 0.95, 0.95)),
                Pickable::IGNORE,
            ));
        });
}
pub(crate) fn checkbox(parent: &mut ChildSpawnerCommands, c: &Ctx, on: bool) {
    parent
        .spawn((
            Node {
                width: c.px(16.),
                height: c.px(16.),
                border: UiRect::all(c.px(1.)),
                border_radius: BorderRadius::all(c.px(3.)),
                justify_content: JustifyContent::Center,
                align_items: AlignItems::Center,
                ..default()
            },
            BackgroundColor(NORMAL),
            BorderColor::all(Color::srgba(0.875, 0.875, 0.875, 0.6)),
            Pickable::IGNORE,
        ))
        .with_children(|p| {
            if on {
                p.spawn((
                    Node {
                        width: c.px(8.),
                        height: c.px(8.),
                        border_radius: BorderRadius::all(c.px(1.)),
                        ..default()
                    },
                    BackgroundColor(FONT_HOVER),
                    Pickable::IGNORE,
                ));
            }
        });
}
