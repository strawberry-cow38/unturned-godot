//! SettingsScreen (Graphics / Controls / Key Binds tabs), GraphicsPanel.Build
//! (the main menu's combined Graphics panel) and KeybindMenu. Rows edit the
//! shared `Settings`; `crate::graphics` owns every renderer/window side effect.
//! Every change persists immediately, as GraphicsOptions.Save/Keybinds.Set do.
use super::godot::{self, Ctx, Skin, look, ui_theme};
use super::*;
use crate::graphics::{Applied, Boot};
use crate::settings::{AntiAliasing, Fullscreen, Graphics, Quality, next};
use bevy::ui_widgets::{Slider, SliderRange, SliderThumb, SliderValue, ValueChange};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tab {
    #[default]
    Graphics,
    Controls,
    KeyBinds,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Layout {
    /// PauseMenu's SettingsScreen.
    #[default]
    Tabs,
    /// MainMenu Configuration > Graphics: GraphicsPanel.Build.
    Combined,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Tone {
    #[default]
    Dim,
    Accent,
    Good,
    Bad,
}
impl Tone {
    fn color(self) -> Color {
        match self {
            Self::Dim => ui_theme::TEXT_DIM,
            Self::Accent => ui_theme::ACCENT,
            Self::Good => ui_theme::GOOD,
            Self::Bad => ui_theme::BAD,
        }
    }
}
pub const HINT: &str =
    "click a binding, then press any key or mouse button  \u{b7}  Del unbinds  \u{b7}  Esc cancels";
pub const HINT_CANCELLED: &str = "click a binding, then press any key or mouse button  \u{b7}  Esc cancels";
const VEHICLE_NOTE: &str = "Vehicles use your Move Forward/Back/Left/Right binds for throttle & steering \u{2014} rebinding movement moves them too. Handbrake is bindable on its own, below.";
const ANISO_NOTE: &str = "Anisotropic filtering has no effect yet \u{2014} no material requests it.";
/// Native counterpart of the source's own "no effect yet" honesty note.
pub const PENDING_NOTE: &str = "Render scale, render distance, sun shafts, fog quality, screen-space reflections and outline are saved, but the native renderer does not draw them yet.";

/// One cycling GraphicsPanel row.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Row {
    AntiAliasing,
    Anisotropy,
    Resolution,
    Shadows,
    ShadowDistance,
    RenderScale,
    RenderDistance,
    Fullscreen,
    VSync,
    TargetFps,
    UiScale,
    EffectQuality,
    AmbientOcclusion,
    Bloom,
    Chromatic,
    ChromaticAmount,
    SunShafts,
    FogQuality,
    Reflections,
    Planar,
    Scope,
    Outline,
    Grass,
    Wind,
    Ragdolls,
    RenderThread,
    InvertLook,
    HeliPitch,
    PlanePitch,
}
/// BuildGraphics order (the pause tab omits the render-thread row).
pub const GRAPHICS_ROWS: [Row; 25] = [
    Row::AntiAliasing,
    Row::Anisotropy,
    Row::Resolution,
    Row::Shadows,
    Row::ShadowDistance,
    Row::RenderScale,
    Row::RenderDistance,
    Row::Fullscreen,
    Row::VSync,
    Row::TargetFps,
    Row::UiScale,
    Row::EffectQuality,
    Row::AmbientOcclusion,
    Row::Bloom,
    Row::Chromatic,
    Row::ChromaticAmount,
    Row::SunShafts,
    Row::FogQuality,
    Row::Reflections,
    Row::Planar,
    Row::Scope,
    Row::Outline,
    Row::Grass,
    Row::Wind,
    Row::Ragdolls,
];
impl Row {
    pub fn name(self) -> &'static str {
        match self {
            Self::AntiAliasing => "Anti-aliasing",
            Self::Anisotropy => "Anisotropic filtering",
            Self::Resolution => "Resolution",
            Self::Shadows => "Shadow quality",
            Self::ShadowDistance => "Shadow distance",
            Self::RenderScale => "Render scale",
            Self::RenderDistance => "Render distance",
            Self::Fullscreen => "Fullscreen",
            Self::VSync => "V-Sync",
            Self::TargetFps => "Target frame rate",
            Self::UiScale => "UI scale",
            Self::EffectQuality => "Effect quality (next map)",
            Self::AmbientOcclusion => "Ambient occlusion",
            Self::Bloom => "Bloom",
            Self::Chromatic => "Chromatic Aberration",
            Self::ChromaticAmount => "Aberration Strength",
            Self::SunShafts => "Sun shafts",
            Self::FogQuality => "Fog quality (restart)",
            Self::Reflections => "Screen-space reflections",
            Self::Planar => "Planar water reflection",
            Self::Scope => "Scope quality",
            Self::Outline => "Outline",
            Self::Grass => "Grass displacement",
            Self::Wind => "Wind",
            Self::Ragdolls => "Ragdolls",
            Self::RenderThread => "Render thread (restart)",
            Self::InvertLook => "Invert look Y",
            Self::HeliPitch => "Helicopter pitch",
            Self::PlanePitch => "Plane pitch",
        }
    }
    pub fn value(self, s: &Settings, boot: Option<&Boot>, applied: Option<&Applied>) -> String {
        let g = &s.graphics;
        let on = |b: bool| if b { "On" } else { "Off" }.to_owned();
        let flip = |b: bool| if b { "Inverted" } else { "Regular" }.to_owned();
        match self {
            Self::AntiAliasing => {
                let label = g.anti_aliasing.label().to_owned();
                match applied.and_then(|a| a.msaa_fallback.map(|_| a.msaa)) {
                    Some(ran) if ran > 1 => format!("{label} ({ran}x)"),
                    Some(_) => format!("{label} (unsupported)"),
                    None => label,
                }
            }
            Self::Anisotropy => format!("{}x", g.anisotropy),
            Self::Resolution => g.resolution_label(),
            Self::Shadows => g.shadows.label().into(),
            Self::ShadowDistance => format!("{:.0} m", g.shadow_distance),
            Self::RenderScale => g.render_scale_label(),
            Self::RenderDistance => g.draw_label().into(),
            Self::Fullscreen => g.fullscreen.label().into(),
            Self::VSync => on(s.vsync),
            Self::TargetFps => {
                if g.target_fps == 0 {
                    "Uncapped".into()
                } else {
                    g.target_fps.to_string()
                }
            }
            Self::UiScale => format!("{:.0}%", g.ui_scale * 100.),
            Self::EffectQuality => g.effect_quality.label().into(),
            Self::AmbientOcclusion => {
                if g.ambient_occlusion && applied.is_some_and(|a| a.msaa > 1) {
                    "On (needs no MSAA)".into()
                } else {
                    on(g.ambient_occlusion)
                }
            }
            Self::Bloom => on(g.bloom),
            Self::Chromatic => on(g.chromatic_aberration),
            Self::ChromaticAmount => format!("{:.2}", g.chromatic_amount),
            Self::SunShafts => on(g.sun_shafts),
            Self::FogQuality => {
                let name = Graphics::FOG_QUALITY[g.fog_quality.min(2) as usize];
                restart(name, boot.is_some_and(|b| b.fog_quality != g.fog_quality))
            }
            Self::Reflections => on(g.screen_space_reflections),
            Self::Planar => g.planar_reflection.label().into(),
            Self::Scope => g.scope_quality.label().into(),
            Self::Outline => on(g.outline),
            Self::Grass => on(g.grass_displacement),
            Self::Wind => on(g.wind),
            Self::Ragdolls => on(g.ragdolls),
            Self::RenderThread => restart(
                if g.render_thread { "Multi" } else { "Single" },
                boot.is_some_and(|b| b.render_thread != g.render_thread),
            ),
            Self::InvertLook => flip(s.invert_look_y),
            Self::HeliPitch => flip(s.invert_heli_pitch),
            Self::PlanePitch => flip(s.invert_plane_pitch),
        }
    }
    /// The row's Pressed handler: GraphicsOptions.Next / toggle.
    pub fn advance(self, s: &mut Settings) {
        let g = &mut s.graphics;
        match self {
            Self::AntiAliasing => g.anti_aliasing = next(&AntiAliasing::ORDER, g.anti_aliasing),
            Self::Anisotropy => g.anisotropy = next(&Graphics::ANISOTROPY, g.anisotropy),
            Self::Resolution => g.resolution = next(&Graphics::RESOLUTIONS, g.resolution),
            Self::Shadows => g.shadows = next(&Quality::ORDER, g.shadows),
            Self::ShadowDistance => {
                g.shadow_distance = next(&Graphics::SHADOW_DISTANCES, g.shadow_distance)
            }
            Self::RenderScale => g.render_scale = next(&Graphics::RENDER_SCALES, g.render_scale),
            Self::RenderDistance => {
                g.draw_distance = next(&Graphics::DRAW_DISTANCES, g.draw_distance)
            }
            Self::Fullscreen => g.fullscreen = next(&Fullscreen::ORDER, g.fullscreen),
            Self::VSync => s.vsync = !s.vsync,
            Self::TargetFps => g.target_fps = next(&Graphics::TARGET_FPS, g.target_fps),
            Self::UiScale => g.ui_scale = next(&Graphics::UI_SCALES, g.ui_scale),
            Self::EffectQuality => g.effect_quality = next(&Quality::ORDER, g.effect_quality),
            Self::AmbientOcclusion => g.ambient_occlusion = !g.ambient_occlusion,
            Self::Bloom => g.bloom = !g.bloom,
            Self::Chromatic => g.chromatic_aberration = !g.chromatic_aberration,
            Self::ChromaticAmount => {
                g.chromatic_amount = next(&Graphics::CHROMATIC, g.chromatic_amount)
            }
            Self::SunShafts => g.sun_shafts = !g.sun_shafts,
            Self::FogQuality => g.fog_quality = (g.fog_quality + 1) % 3,
            Self::Reflections => g.screen_space_reflections = !g.screen_space_reflections,
            Self::Planar => g.planar_reflection = next(&Quality::ORDER, g.planar_reflection),
            Self::Scope => g.scope_quality = next(&Quality::ORDER_NO_OFF, g.scope_quality),
            Self::Outline => g.outline = !g.outline,
            Self::Grass => g.grass_displacement = !g.grass_displacement,
            Self::Wind => g.wind = !g.wind,
            Self::Ragdolls => g.ragdolls = !g.ragdolls,
            Self::RenderThread => g.render_thread = !g.render_thread,
            Self::InvertLook => s.invert_look_y = !s.invert_look_y,
            Self::HeliPitch => s.invert_heli_pitch = !s.invert_heli_pitch,
            Self::PlanePitch => s.invert_plane_pitch = !s.invert_plane_pitch,
        }
    }
}
fn restart(name: &str, pending: bool) -> String {
    if pending {
        format!("{name} (restart)")
    } else {
        name.into()
    }
}

/// A continuous SliderRow (HSlider + live readout).
#[derive(Component, Clone, Copy, Debug, PartialEq, Eq)]
pub enum SliderKind {
    Look,
    Heli,
    /// Native additions kept from the earlier native settings (not in the source).
    Fov,
    Volume,
}
impl SliderKind {
    pub fn name(self) -> &'static str {
        match self {
            Self::Look => "Look sensitivity",
            Self::Heli => "Helicopter sensitivity",
            Self::Fov => "Field of view",
            Self::Volume => "Master volume",
        }
    }
    /// (min, max, step) from ControlsOptions / the native clamps.
    pub fn range(self) -> (f32, f32, f32) {
        match self {
            Self::Look => (0.02, 0.5, 0.01),
            Self::Heli => (0.25, 2.5, 0.05),
            Self::Fov => (50., 110., 1.),
            Self::Volume => (0., 1., 0.05),
        }
    }
    pub fn get(self, s: &Settings) -> f32 {
        match self {
            Self::Look => s.mouse_sensitivity,
            Self::Heli => s.heli_sensitivity,
            Self::Fov => s.field_of_view,
            Self::Volume => s.master_volume,
        }
    }
    pub fn set(self, s: &mut Settings, v: f32) {
        match self {
            Self::Look => s.mouse_sensitivity = v,
            Self::Heli => s.heli_sensitivity = v,
            Self::Fov => s.field_of_view = v,
            Self::Volume => s.master_volume = v,
        }
        s.normalize();
    }
    pub fn label(self, v: f32) -> String {
        match self {
            Self::Look => format!("{v:.2}"),
            Self::Heli => format!("{v:.2}x"),
            Self::Fov => format!("{v:.0}\u{b0}"),
            Self::Volume => format!("{:.0}%", v * 100.),
        }
    }
    /// Godot Range snaps to Step from min.
    pub fn snap(self, v: f32) -> f32 {
        let (min, max, step) = self.range();
        (min + ((v - min) / step).round() * step).clamp(min, max)
    }
}
#[derive(Component)]
struct Readout(SliderKind);

#[derive(Component, Clone, Copy, Debug)]
pub enum Action {
    Cycle(Row),
    Tab(Tab),
    Bind(GameAction),
    ResetBinds,
    Back,
}

pub(super) struct OptionsPlugin;
impl Plugin for OptionsPlugin {
    fn build(&self, app: &mut App) {
        godot::init_fonts(app);
        app.init_resource::<godot::ScrollMemory>()
            .add_observer(godot::restore_scroll)
            .add_observer(activate)
            .add_observer(slide)
            .add_systems(
                Update,
                (thumbs, godot::appearance, godot::scroll_visibility, godot::remember_scroll)
                    .after(rebuild_overlay)
                    .in_set(UiUpdate),
            );
    }
}

/// Persist on every change and report a failure where the player is looking.
pub(crate) fn persist(settings: &Settings, state: &mut UiState) {
    if let Err(error) = settings.save() {
        state.settings_message = format!("Could not save settings: {error}");
        state.settings_tone = Tone::Bad;
    }
}

pub(super) fn activate(
    event: On<Activate>,
    actions: Query<&Action>,
    mut state: ResMut<UiState>,
    mut settings: ResMut<Settings>,
) {
    let Ok(action) = actions.get(event.entity) else {
        return;
    };
    match *action {
        Action::Cycle(row) => {
            row.advance(&mut settings);
            persist(&settings, &mut state);
        }
        Action::Tab(tab) => state.settings_tab = tab,
        Action::Bind(target) => {
            state.capturing = Some(target);
            state.settings_message = format!(
                "press a key or mouse button for {}  \u{b7}  Esc cancels",
                target.display_name()
            );
            state.settings_tone = Tone::Accent;
        }
        Action::ResetBinds => {
            settings.bindings = Settings::default().bindings;
            persist(&settings, &mut state);
        }
        Action::Back => {
            state.capturing = None;
            state.close_overlay();
        }
    }
}

/// Snapped live value while dragging; committed (and saved) on release.
fn slide(
    change: On<ValueChange<f32>>,
    kinds: Query<&SliderKind>,
    mut commands: Commands,
    mut settings: ResMut<Settings>,
    mut state: ResMut<UiState>,
    mut readouts: Query<(&Readout, &mut Text)>,
) {
    let Ok(kind) = kinds.get(change.source) else {
        return;
    };
    let value = kind.snap(change.value);
    commands.entity(change.source).insert(SliderValue(value));
    for (readout, mut text) in &mut readouts {
        if readout.0 == *kind {
            text.0 = kind.label(value);
        }
    }
    if change.is_final {
        kind.set(&mut settings, value);
        persist(&settings, &mut state);
    }
}

/// The core slider leaves the thumb to the app: travel is width minus thumb.
fn thumbs(
    sliders: Query<(&SliderValue, &SliderRange, &Children), With<SliderKind>>,
    lanes: Query<&Children, Without<SliderKind>>,
    mut nodes: Query<&mut Node, With<SliderThumb>>,
) {
    for (value, range, children) in &sliders {
        let position = range.thumb_position(value.0) * 100.;
        for lane in children {
            let Ok(lane) = lanes.get(*lane) else { continue };
            for thumb in lane {
                if let Ok(mut node) = nodes.get_mut(*thumb) {
                    if node.left != percent(position) {
                        node.left = percent(position);
                    }
                }
            }
        }
    }
}

/// The settings view for `state.settings_layout`.
pub(super) fn screen(
    root: &mut ChildSpawnerCommands,
    c: &Ctx,
    state: &UiState,
    settings: &Settings,
    boot: Option<&Boot>,
    applied: Option<&Applied>,
) {
    match state.settings_layout {
        Layout::Combined => combined(root, c, settings, boot, applied),
        Layout::Tabs => tabs(root, c, state, settings, boot, applied),
    }
}

fn frame(c: &Ctx) -> (Node, Node) {
    (
        Node {
            padding: UiRect::all(c.px(24.)),
            ..default()
        },
        Node {
            min_width: c.px(420.),
            flex_direction: FlexDirection::Column,
            row_gap: c.px(10.),
            ..default()
        },
    )
}

/// GraphicsPanel.Build: GRAPHICS rows, render thread, CONTROLS, note, Back.
fn combined(p: &mut ChildSpawnerCommands, c: &Ctx, s: &Settings, boot: Option<&Boot>, applied: Option<&Applied>) {
    p.spawn((Node::default(), godot::panel_color())).with_children(|panel| {
        let (margin, column) = frame(c);
        panel.spawn(margin).with_children(|m| {
            godot::scroll_box(m, c, "combined", c.px(470.), c.px(640.), |scroll| {
                scroll.spawn(column).with_children(|v| {
                    v.spawn((c.text("GRAPHICS", 28., Color::WHITE), Node { align_self: AlignSelf::Center, ..default() }));
                    for row in GRAPHICS_ROWS.iter().copied().chain([Row::RenderThread]) {
                        cycle_row(v, c, row, s, boot, applied);
                    }
                    v.spawn((c.text("CONTROLS", 22., Color::WHITE), Node { align_self: AlignSelf::Center, ..default() }));
                    cycle_row(v, c, Row::HeliPitch, s, boot, applied);
                    cycle_row(v, c, Row::PlanePitch, s, boot, applied);
                    slider_row(v, c, SliderKind::Heli, s);
                    note(v, c, ANISO_NOTE);
                    note(v, c, PENDING_NOTE);
                    godot::label_button(
                        v,
                        c,
                        look(Skin::Default),
                        Node { min_height: c.px(40.), ..default() },
                        "Back",
                        16.,
                        Some(Action::Back),
                    );
                });
            });
        });
    });
}

fn note(p: &mut ChildSpawnerCommands, c: &Ctx, text: &str) {
    p.spawn((
        c.text(text, 12., Color::srgba(1., 1., 1., 0.45)),
        TextLayout::justify(Justify::Center),
        Node { width: c.px(420.), ..default() },
    ));
}

fn cycle_row(p: &mut ChildSpawnerCommands, c: &Ctx, row: Row, s: &Settings, boot: Option<&Boot>, applied: Option<&Applied>) {
    p.spawn(Node {
        flex_direction: FlexDirection::Row,
        column_gap: c.px(12.),
        align_items: AlignItems::Center,
        ..default()
    })
    .with_children(|h| {
        h.spawn((c.text(row.name(), 13., ui_theme::TEXT), Node { flex_grow: 1., ..default() }));
        godot::label_button(
            h,
            c,
            look(Skin::Theme),
            Node {
                min_width: c.px(150.),
                min_height: c.px(34.),
                padding: UiRect::axes(c.px(4.), c.px(4.)),
                ..default()
            },
            row.value(s, boot, applied),
            13.,
            Some(Action::Cycle(row)),
        );
    });
}

fn slider_row(p: &mut ChildSpawnerCommands, c: &Ctx, kind: SliderKind, s: &Settings) {
    let (min, max, step) = kind.range();
    let value = kind.get(s);
    p.spawn(Node {
        flex_direction: FlexDirection::Row,
        column_gap: c.px(12.),
        align_items: AlignItems::Center,
        ..default()
    })
    .with_children(|h| {
        h.spawn((c.text(kind.name(), 13., ui_theme::TEXT), Node { flex_grow: 1., ..default() }));
        let thumb = 16.;
        h.spawn((
            Slider::default(),
            kind,
            SliderValue(value),
            SliderRange::new(min, max),
            bevy::ui_widgets::SliderStep(step),
            Hovered::default(),
            Node {
                width: c.px(150.),
                height: c.px(34.),
                align_items: AlignItems::Center,
                padding: UiRect::right(c.px(thumb)),
                ..default()
            },
        ))
        .with_children(|slider| {
            // Lane: the thumb travels its full width (track minus thumb).
            slider
                .spawn((
                    Node {
                        width: percent(100),
                        height: c.px(thumb),
                        align_items: AlignItems::Center,
                        ..default()
                    },
                    Pickable::IGNORE,
                ))
                .with_children(|lane| {
                    lane.spawn((
                        Node {
                            position_type: PositionType::Absolute,
                            left: px(0),
                            right: c.px(-thumb),
                            height: c.px(4.),
                            border_radius: BorderRadius::all(c.px(2.)),
                            ..default()
                        },
                        BackgroundColor(Color::srgba(1., 1., 1., 0.2)),
                        Pickable::IGNORE,
                    ));
                    lane.spawn((
                        SliderThumb,
                        Node {
                            position_type: PositionType::Absolute,
                            left: percent(SliderRange::new(min, max).thumb_position(value) * 100.),
                            width: c.px(thumb),
                            height: c.px(thumb),
                            border_radius: BorderRadius::all(c.px(thumb / 2.)),
                            ..default()
                        },
                        BackgroundColor(Color::srgb(0.95, 0.95, 0.95)),
                    ));
                });
        });
        h.spawn((
            Readout(kind),
            c.text(kind.label(value), 13., ui_theme::TEXT),
            TextLayout::justify(Justify::Right),
            Node { min_width: c.px(56.), ..default() },
        ));
    });
}

/// SettingsScreen: title, UITheme strip of tabs, the selected tab, primary Back.
fn tabs(p: &mut ChildSpawnerCommands, c: &Ctx, state: &UiState, s: &Settings, boot: Option<&Boot>, applied: Option<&Applied>) {
    p.spawn((
        Node {
            padding: UiRect::all(c.px(18.)),
            border_radius: BorderRadius::all(c.px(6.)),
            ..default()
        },
        BackgroundColor(ui_theme::BG_SOLID),
    ))
    .with_children(|panel| {
        panel
            .spawn(Node {
                min_width: c.px(600.),
                flex_direction: FlexDirection::Column,
                row_gap: c.px(6.),
                ..default()
            })
            .with_children(|root| {
                root.spawn((c.text("SETTINGS", 20., ui_theme::TEXT), Node { align_self: AlignSelf::Center, ..default() }));
                root.spawn((
                    Node {
                        flex_direction: FlexDirection::Row,
                        column_gap: c.px(6.),
                        border_radius: BorderRadius::all(c.px(4.)),
                        ..default()
                    },
                    BackgroundColor(ui_theme::bar()),
                ))
                .with_children(|strip| {
                    for (tab, name) in [
                        (Tab::Graphics, "Graphics"),
                        (Tab::Controls, "Controls"),
                        (Tab::KeyBinds, "Key Binds"),
                    ] {
                        let mut tab_look = look(Skin::Theme);
                        tab_look.font = Some(if state.settings_tab == tab {
                            ui_theme::ACCENT
                        } else {
                            ui_theme::TEXT_DIM
                        });
                        godot::label_button(
                            strip,
                            c,
                            tab_look,
                            Node {
                                min_width: c.px(140.),
                                min_height: c.px(36.),
                                flex_grow: 1.,
                                ..default()
                            },
                            name,
                            13.,
                            Some(Action::Tab(tab)),
                        );
                    }
                });
                root.spawn(Node {
                    padding: UiRect::vertical(c.px(6.)),
                    ..default()
                })
                .with_children(|content| match state.settings_tab {
                    Tab::Graphics => {
                        let (margin, column) = frame(c);
                        content.spawn(margin).with_children(|m| {
                            godot::scroll_box(m, c, "graphics", c.px(470.), c.px(640.), |scroll| {
                                scroll.spawn(column).with_children(|v| {
                                    for row in GRAPHICS_ROWS {
                                        cycle_row(v, c, row, s, boot, applied);
                                    }
                                    note(v, c, ANISO_NOTE);
                                    note(v, c, PENDING_NOTE);
                                });
                            });
                        });
                    }
                    Tab::Controls => {
                        let (margin, column) = frame(c);
                        content.spawn(margin).with_children(|m| {
                            godot::scroll_box(m, c, "controls", c.px(470.), c.px(640.), |scroll| {
                                scroll.spawn(column).with_children(|v| {
                                    slider_row(v, c, SliderKind::Look, s);
                                    cycle_row(v, c, Row::InvertLook, s, boot, applied);
                                    cycle_row(v, c, Row::HeliPitch, s, boot, applied);
                                    cycle_row(v, c, Row::PlanePitch, s, boot, applied);
                                    slider_row(v, c, SliderKind::Heli, s);
                                    // Kept from the earlier native screen; the source has neither row.
                                    v.spawn(c.text("NATIVE", 12., ui_theme::TEXT_DIM));
                                    slider_row(v, c, SliderKind::Fov, s);
                                    slider_row(v, c, SliderKind::Volume, s);
                                });
                            });
                        });
                    }
                    Tab::KeyBinds => key_binds(content, c, state, s),
                });
                if state.settings_tab != Tab::KeyBinds && state.settings_tone == Tone::Bad {
                    root.spawn(c.text(&state.settings_message, 12., ui_theme::BAD));
                }
                godot::label_button(
                    root,
                    c,
                    look(Skin::Primary),
                    Node { min_height: c.px(42.), ..default() },
                    "Back",
                    13.,
                    Some(Action::Back),
                );
            });
    });
}

/// KeybindMenu: every action with its context, capture-by-press, reset.
fn key_binds(p: &mut ChildSpawnerCommands, c: &Ctx, state: &UiState, s: &Settings) {
    p.spawn(Node {
        min_width: c.px(560.),
        flex_direction: FlexDirection::Column,
        row_gap: c.px(6.),
        ..default()
    })
    .with_children(|outer| {
        outer.spawn(c.text("KEY BINDS", 20., ui_theme::TEXT));
        let (hint, tone) = if state.settings_message.is_empty() {
            (HINT, Tone::Dim)
        } else {
            (state.settings_message.as_str(), state.settings_tone)
        };
        outer.spawn(c.text(hint, 12., tone.color()));
        outer.spawn(c.wrapped(VEHICLE_NOTE, 12., ui_theme::TEXT_DIM, 560.));
        godot::scroll_box(outer, c, "keybinds", percent(100), c.px(420.), |scroll| {
                scroll
                    .spawn(Node {
                        flex_direction: FlexDirection::Column,
                        row_gap: c.px(2.),
                        ..default()
                    })
                    .with_children(|list| {
                        for (action, binding) in &s.bindings {
                            list.spawn(Node {
                                flex_direction: FlexDirection::Row,
                                column_gap: c.px(6.),
                                align_items: AlignItems::Center,
                                ..default()
                            })
                            .with_children(|row| {
                                let context = action.context_label();
                                let name = if context.is_empty() {
                                    action.display_name()
                                } else {
                                    format!("{}  \u{b7}  {context}", action.display_name())
                                };
                                row.spawn((
                                    c.text(name, 13., ui_theme::TEXT),
                                    Node { min_width: c.px(240.), min_height: c.px(30.), align_items: AlignItems::Center, ..default() },
                                ));
                                godot::label_button(
                                    row,
                                    c,
                                    look(Skin::Theme),
                                    Node { min_width: c.px(180.), min_height: c.px(30.), ..default() },
                                    if state.capturing == Some(*action) {
                                        "press\u{2026}".into()
                                    } else {
                                        binding.source_label()
                                    },
                                    13.,
                                    Some(Action::Bind(*action)),
                                );
                            });
                        }
                    });
            });
        outer.spawn(Node::default()).with_children(|footer| {
            godot::label_button(
                footer,
                c,
                look(Skin::Theme),
                Node { min_width: c.px(180.), min_height: c.px(36.), ..default() },
                "Reset to defaults",
                13.,
                Some(Action::ResetBinds),
            );
        });
    });
}
