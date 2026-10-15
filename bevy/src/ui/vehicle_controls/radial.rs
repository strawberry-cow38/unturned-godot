//! LightbarRadial selection and the original AmmoPie geometry/animation.
use super::*;
use bevy::{asset::embedded_asset, render::render_resource::AsBindGroup, shader::ShaderRef};
pub(super) const HOLD_MILLIS: u128 = 220;
const SCALE: f32 = 1.9;
const INNER: f32 = 45. * SCALE;
const OUTER: f32 = 172. * SCALE;
#[derive(Clone)]
pub(super) struct Selection {
    pub id: u32,
    pub highlight: Option<usize>,
    pub on: bool,
    pub pattern: usize,
    pub open: f32,
    pub pop: Vec4,
}
impl Selection {
    pub fn new(id: u32, row: &Value) -> Self {
        let on = row["flags"].as_u64().unwrap_or(0) & 8 != 0;
        let pattern = row["lightbarPattern"].as_u64().unwrap_or(0).min(2) as usize;
        Self {
            id,
            highlight: Some(if on { pattern } else { 0 }),
            on,
            pattern,
            open: 0.,
            pop: Vec4::ZERO,
        }
    }
    pub fn point(&mut self, p: Vec2, viewport: Vec2) {
        let v = (p - viewport * 0.5) / fit(viewport);
        self.highlight = if v.length() < INNER {
            None
        } else {
            let dirs = [Vec2::NEG_Y, Vec2::X, Vec2::Y, Vec2::NEG_X];
            let mut best = -2.;
            let mut chosen = 0;
            for (i, d) in dirs.iter().enumerate() {
                let dot = v.normalize().dot(*d);
                if dot > best {
                    best = dot;
                    chosen = i;
                }
            }
            Some(chosen)
        };
    }
    pub fn confirm(&self) -> Option<i32> {
        self.highlight
            .filter(|i| *i < 3 || self.on)
            .map(|i| if i == 3 { -1 } else { i as i32 })
    }
    pub(super) fn advance(&mut self, dt: f32) {
        self.open = (self.open + dt / 0.12).min(1.);
        let k = 1. - (-22. * dt).exp();
        for i in 0..4 {
            let target = if self.highlight == Some(i) && (i < 3 || self.on) {
                1.
            } else {
                0.
            };
            self.pop[i] += (target - self.pop[i]) * k;
            if (self.pop[i] - target).abs() < 0.002 {
                self.pop[i] = target;
            }
        }
    }
}
// Keep the source proportions and label chip inside small native viewports.
// At 1080p this is exactly the original 1.9 scale.
fn fit(viewport: Vec2) -> f32 {
    (viewport.y / (2. * (OUTER + 30. * SCALE + 52. * SCALE + 12.)))
        .min(viewport.x / (2. * (OUTER + 24.)))
        .min(1.)
}
fn ease(x: f32) -> f32 {
    1. - (1. - x).powi(3)
}
#[derive(Component)]
struct Root;
#[derive(Component)]
struct Label(usize);
#[derive(Component)]
struct Hub;
#[derive(Component)]
struct Chip;
#[derive(Component)]
struct ChipLine(bool);
#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
struct PieMaterial {
    #[uniform(0)]
    size: Vec4,
    #[uniform(1)]
    state: Vec4,
    #[uniform(2)]
    pop: Vec4,
}
impl UiMaterial for PieMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/ui/vehicle_controls/pie.wesl".into()
    }
}
pub(super) struct RadialPlugin;
impl Plugin for RadialPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "pie.wesl");
        app.add_plugins(UiMaterialPlugin::<PieMaterial>::default())
            .add_systems(Update, (build, update).chain().after(UiUpdate));
    }
}
fn build(
    mut commands: Commands,
    roots: Query<Entity, With<Root>>,
    ui: Res<UiState>,
    menu: Res<Menu>,
    mut materials: ResMut<Assets<PieMaterial>>,
    assets: Res<AssetServer>,
) {
    if ui.screen != Screen::Lightbar || menu.radial.is_none() {
        for e in &roots {
            commands.entity(e).despawn();
        }
        return;
    }
    if !roots.is_empty() {
        return;
    }
    commands
        .spawn((
            Root,
            crate::ui::backdrop::Backdrop::ammo_pie(0.),
            Node {
                position_type: PositionType::Absolute,
                width: percent(100),
                height: percent(100),
                ..default()
            },
            GlobalZIndex(60),
        ))
        .with_children(|p| {
            p.spawn((
                Node {
                    position_type: PositionType::Absolute,
                    width: percent(100),
                    height: percent(100),
                    ..default()
                },
                MaterialNode(materials.add(PieMaterial {
                    size: Vec4::ZERO,
                    state: Vec4::ZERO,
                    pop: Vec4::ZERO,
                })),
            ));
            for i in 0..4 {
                p.spawn((
                    Label(i),
                    text("", 12. * SCALE, TEXT),
                    Node {
                        position_type: PositionType::Absolute,
                        width: px(140. * SCALE),
                        justify_content: JustifyContent::Center,
                        ..default()
                    },
                    TextLayout::justify(Justify::Center),
                ));
            }
            p.spawn((
                Label(4),
                ImageNode {
                    image: assets.load("ui/radial_unload.png"),
                    color: Color::srgb(1., 0.66, 0.60),
                    ..default()
                },
                Node {
                    position_type: PositionType::Absolute,
                    ..default()
                },
            ));
            p.spawn((
                Hub,
                text("LIGHTBAR", 11. * SCALE, DIM),
                Node {
                    position_type: PositionType::Absolute,
                    width: px(120. * SCALE),
                    ..default()
                },
                TextLayout::justify(Justify::Center),
            ));
            p.spawn((
                Chip,
                Node {
                    position_type: PositionType::Absolute,
                    left: percent(50),
                    flex_direction: FlexDirection::Column,
                    align_items: AlignItems::Center,
                    border: UiRect::all(px(2)),
                    border_radius: BorderRadius::all(px(10)),
                    ..default()
                },
                UiTransform::from_translation(Val2::percent(-50, 0)),
                BackgroundColor(Color::srgba(0.04, 0.05, 0.07, 0.92)),
                BorderColor::all(Color::srgba(0.4, 0.62, 0.9, 0.85)),
            ))
            .with_children(|p| {
                for detail in [false, true] {
                    p.spawn((
                        ChipLine(detail),
                        text("", 15. * SCALE, TEXT),
                        TextLayout::new(Justify::Center, LineBreak::NoWrap),
                        Node::default(),
                    ));
                }
            });
        });
}
fn update(
    menu: ResMut<Menu>,
    ui: Res<UiState>,
    time: Res<Time<Real>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    catalog: Res<ItemCatalog>,
    nodes: Query<&MaterialNode<PieMaterial>>,
    mut materials: ResMut<Assets<PieMaterial>>,
    mut backdrops: Query<&mut crate::ui::backdrop::Backdrop, With<Root>>,
    mut labels: Query<
        (
            &Label,
            &mut Node,
            Option<&mut Text>,
            Option<&mut ImageNode>,
            Option<&mut TextFont>,
            Option<&mut TextColor>,
        ),
        (Without<Hub>, Without<Chip>, Without<ChipLine>),
    >,
    mut hub: Query<(&mut Text, &mut Node, &mut TextFont, &mut TextColor), With<Hub>>,
    mut chip: Query<
        (
            &mut Node,
            &mut Visibility,
            &mut BackgroundColor,
            &mut BorderColor,
        ),
        (With<Chip>, Without<Hub>, Without<Label>),
    >,
    mut chip_lines: Query<
        (
            &ChipLine,
            &mut Text,
            &mut Node,
            &mut TextFont,
            &mut TextColor,
        ),
        (Without<Chip>, Without<Hub>, Without<Label>),
    >,
) {
    let mut menu = menu;
    let Some(r) = menu
        .radial
        .as_mut()
        .filter(|_| ui.screen == Screen::Lightbar)
    else {
        return;
    };
    let Ok(w) = windows.single() else {
        return;
    };
    let vp = Vec2::new(w.width(), w.height());
    let scale = fit(vp);
    r.advance(time.delta_secs());
    let e = ease(r.open);
    for mut backdrop in &mut backdrops {
        *backdrop = crate::ui::backdrop::Backdrop::ammo_pie(e);
    }
    let sc = 0.92 + 0.08 * e;
    let c = vp * 0.5;
    for node in &nodes {
        if let Some(mut m) = materials.get_mut(&node.0) {
            m.size = Vec4::new(vp.x, vp.y, scale, e);
            m.state = Vec4::new(
                r.highlight.map_or(-1., |i| i as f32),
                if r.on { 1. } else { 0. },
                r.pattern as f32,
                sc,
            );
            m.pop = r.pop.map(ease);
        }
    }
    let names = catalog.vehicle_lighting.as_ref().map(|d| &d.pattern_names);
    for (label, mut node, text, image, font, color) in &mut labels {
        let i = label.0.min(3);
        let angle = -(std::f32::consts::PI * 0.5) + i as f32 * std::f32::consts::FRAC_PI_2;
        let pop = ease(r.pop[i]);
        let p = c + Vec2::new(angle.cos(), angle.sin())
            * ((INNER * sc + OUTER * sc + 16. * pop) * 0.5 * scale);
        if let Some(mut text) = text {
            **text = if i == 3 {
                "LIGHTBAR OFF".into()
            } else if r.on && r.pattern == i {
                "on".into()
            } else {
                String::new()
            };
        }
        if let Some(mut f) = font {
            f.font_size = FontSize::Px(12. * SCALE * scale);
        }
        if let Some(mut color) = color {
            color.0 = if i == 3 && !r.on {
                Color::srgba(0.55, 0.56, 0.6, e)
            } else if r.pop[i] > 0.01 {
                Color::srgba(0.96, 0.98, 1., e)
            } else if i == 3 {
                Color::srgba(0.86, 0.88, 0.92, e)
            } else {
                Color::srgba(0.70, 0.9, 0.72, e)
            };
        }
        if let Some(mut image) = image {
            let size = 54. * SCALE * (1. + 0.1 * pop) * scale;
            node.width = px(size);
            node.height = px(size);
            node.left = px(p.x - size * 0.5);
            node.top = px(p.y - size * 0.5 - 12. * SCALE * scale);
            image.color = if r.on {
                Color::srgba(1., 0.66, 0.6, e)
            } else {
                Color::srgba(0.6, 0.58, 0.6, 0.45 * e)
            };
        } else {
            node.width = px(140. * SCALE * scale);
            node.left = px(p.x - 70. * SCALE * scale);
            node.top = px(p.y + 24. * SCALE * scale);
        }
    }
    for (mut text, mut node, mut font, mut color) in &mut hub {
        color.0 = if r.highlight.is_none() {
            Color::srgba(0.94, 0.97, 1., e)
        } else {
            Color::srgba(0.62, 0.66, 0.74, e)
        };
        **text = if r.highlight.is_none() {
            "CANCEL"
        } else {
            "LIGHTBAR"
        }
        .into();
        node.width = px(120. * SCALE * scale);
        node.left = px(c.x - 60. * SCALE * scale);
        node.top = px(c.y - 7. * SCALE * scale);
        font.font_size = FontSize::Px(11. * SCALE * scale);
    }
    let i = r.highlight.unwrap_or(0);
    let selectable = i < 3 || r.on;
    let name = if i == 3 {
        "lightbar off"
    } else {
        names
            .and_then(|n| n.get(i))
            .map(String::as_str)
            .unwrap_or("")
    };
    let detail = if i == 3 && !r.on {
        "off"
    } else if i == r.pattern && r.on {
        "on"
    } else {
        ""
    };
    let unit = SCALE * scale;
    for (mut node, mut visible, mut background, mut border) in &mut chip {
        *visible = if r.highlight.is_some() {
            Visibility::Visible
        } else {
            Visibility::Hidden
        };
        node.top = px(c.y + OUTER * sc * scale + 30. * unit);
        node.height = px(52. * unit);
        node.padding = UiRect {
            left: px(22. * unit),
            right: px(22. * unit),
            top: px(6. * unit),
            bottom: px(4. * unit),
        };
        node.row_gap = px(5. * unit);
        background.0 = Color::srgba(0.04, 0.05, 0.07, 0.92 * e);
        *border = BorderColor::all(if selectable {
            Color::srgba(0.4, 0.62, 0.9, 0.85 * e)
        } else {
            Color::srgba(1., 1., 1., 0.15 * e)
        });
    }
    for (line, mut text, mut node, mut font, mut color) in &mut chip_lines {
        **text = if line.0 { detail } else { name }.into();
        font.font_size = FontSize::Px((if line.0 { 12. } else { 15. }) * unit);
        node.min_height = px((if line.0 { 12. } else { 15. }) * unit);
        color.0 = match (line.0, selectable) {
            (false, true) => Color::srgba(0.94, 0.96, 1., e),
            (false, false) => Color::srgba(0.62, 0.63, 0.67, e),
            (true, true) => Color::srgba(0.70, 0.90, 0.72, e),
            (true, false) => Color::srgba(0.86, 0.52, 0.46, e),
        };
    }
}
