//! Original bonnet panel. Persistent rows read accepted facts; a click requests
//! the canonical repair and never changes local glass, lamp or tire masks.
use super::*;
#[cfg(test)]
use crate::vehicle::access::focus;
use bevy::{input::mouse::MouseWheel, ui::ScrollPosition};
#[cfg(test)]
#[path = "mechanics_tests.rs"]
mod tests;

const ACTION: &str = "openVehicleHood";
#[derive(Component)]
pub(super) struct Root;
#[derive(Component)]
pub(super) struct BodyText;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Part {
    Glass,
    Lamp,
    Tire,
}
impl Part {
    fn present(self, v: &Value) -> u32 {
        mask(
            v,
            match self {
                Self::Glass => "glassMask",
                Self::Lamp => "lampMask",
                Self::Tire => "tireMask",
            },
        )
    }
    fn broken(self, v: &Value) -> u32 {
        mask(
            v,
            match self {
                Self::Glass => "brokenGlassMask",
                Self::Lamp => "brokenLampMask",
                Self::Tire => "poppedTireMask",
            },
        ) & self.present(v)
    }
    fn title(self) -> &'static str {
        match self {
            Self::Glass => "GLAZING",
            Self::Lamp => "LAMPS",
            Self::Tire => "TIRES",
        }
    }
    fn status(self, broken: bool) -> &'static str {
        match (self, broken) {
            (Self::Glass, true) => "SHATTERED",
            (Self::Glass, false) => "intact",
            (Self::Lamp, true) => "SHOT OUT",
            (Self::Lamp, false) => "working",
            (Self::Tire, true) => "BLOWN",
            (Self::Tire, false) => "ok",
        }
    }
    fn display(self, index: u8, count: u8, catalog: &ItemCatalog) -> String {
        match self {
            Self::Glass => catalog
                .vehicle_maintenance
                .as_ref()
                .and_then(|d| d.glass_names.get(index as usize))
                .cloned()
                .unwrap_or_default(),
            Self::Lamp => lamp_display(
                catalog
                    .vehicle_lighting
                    .as_ref()
                    .and_then(|d| d.lamp_labels.get(index as usize))
                    .map(String::as_str)
                    .unwrap_or(""),
            )
            .into(),
            Self::Tire => tire_display(index as usize, count as usize),
        }
    }
}
#[derive(Component)]
pub(super) struct PartText(Part, u8, u8);
#[derive(Component)]
pub(super) struct Fix(u32, Part, u8);
#[derive(Component)]
pub(super) struct FixText(Part, u8);
#[derive(Component)]
pub(super) struct Scroll;

pub(super) fn open(
    focus: Res<InteractionFocus>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    bodies: Query<&VehicleBody>,
    mut menu: ResMut<Menu>,
    mut ui: ResMut<UiState>,
) {
    if ui.captures_input() || !settings.just_pressed(GameAction::Interact, &keys, &mouse) {
        return;
    }
    for body in &bodies {
        if focus.selected(ACTION, body.net_id) {
            menu.mechanics = Some(body.net_id);
            ui.screen = Screen::Mechanics;
            return;
        }
    }
}
pub(super) fn consume_interact(
    settings: &Settings,
    keys: &mut ButtonInput<KeyCode>,
    mouse: &mut ButtonInput<MouseButton>,
) {
    // Consume exactly the rebound edge; held movement and unrelated keys remain.
    match settings.bindings.get(&GameAction::Interact) {
        Some(Binding::Key(key)) => {
            let pressed = keys
                .get_just_pressed()
                .find(|k| format!("{k:?}") == *key)
                .copied();
            if let Some(key) = pressed {
                keys.clear_just_pressed(key);
            }
        }
        Some(Binding::Mouse(button)) => {
            let pressed = mouse
                .get_just_pressed()
                .find(|b| mouse_number(**b) == *button)
                .copied();
            if let Some(button) = pressed {
                mouse.clear_just_pressed(button);
            }
        }
        _ => {}
    }
}
fn lamp_display(label: &str) -> &str {
    match label {
        "headlight_l" => "left headlight",
        "headlight_r" => "right headlight",
        "taillight_l" => "left taillight",
        "taillight_r" => "right taillight",
        "lightbar_l" => "lightbar red lens",
        "lightbar_r" => "lightbar blue lens",
        "lightbar_c" => "lightbar centre",
        _ => label,
    }
}
fn tire_display(i: usize, n: usize) -> String {
    if n < 4 {
        format!("wheel {}", i + 1)
    } else {
        format!(
            "{} {} tire",
            if i % 2 == 0 { "left" } else { "right" },
            if i < 2 { "front" } else { "rear" }
        )
    }
}
fn mask(v: &Value, key: &str) -> u32 {
    v[key].as_u64().unwrap_or(0) as u32
}

pub(super) fn build(
    mut commands: Commands,
    ui: Res<UiState>,
    menu: Res<Menu>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    roots: Query<Entity, With<Root>>,
    bodies: Query<&VehicleBody>,
    mut previous: Local<Option<(u64, u32, u32, u32, u32)>>,
) {
    let view = menu
        .mechanics
        .filter(|_| ui.screen == Screen::Mechanics)
        .and_then(|id| row(&snapshot.0, id).map(|r| (id, r)));
    let Some((id, accepted)) = view else {
        for e in &roots {
            commands.entity(e).despawn();
        }
        *previous = None;
        return;
    };
    let Some(def) = accepted["typeId"]
        .as_u64()
        .and_then(|i| data::definition(i as u8))
    else {
        return;
    };
    let Some(body) = bodies.iter().find(|b| b.net_id == id) else {
        return;
    };
    let glass = Part::Glass.present(accepted);
    let lamps = Part::Lamp.present(accepted);
    let tires = Part::Tire.present(accepted);
    // Catalog arrival may trail the first owner snapshot. Never freeze empty names.
    if (glass != 0 && catalog.vehicle_maintenance.is_none())
        || (lamps != 0 && catalog.vehicle_lighting.is_none())
    {
        return;
    }
    let key = (menu.epoch, id, glass, lamps, tires);
    if *previous == Some(key) && !roots.is_empty() {
        return;
    }
    *previous = Some(key);
    for e in &roots {
        commands.entity(e).despawn();
    }
    commands
        .spawn((
            Root,
            Node {
                position_type: PositionType::Absolute,
                width: percent(100),
                height: percent(100),
                align_items: AlignItems::Center,
                justify_content: JustifyContent::Center,
                ..default()
            },
            GlobalZIndex(55),
            BackgroundColor(Color::srgba(0., 0., 0., 0.45)),
        ))
        .with_children(|p| {
            p.spawn((
                Scroll,
                ScrollPosition::default(),
                Node {
                    width: px(404),
                    max_width: percent(95),
                    max_height: percent(94),
                    padding: UiRect::all(px(22)),
                    flex_direction: FlexDirection::Column,
                    row_gap: px(12),
                    overflow: Overflow::scroll_y(),
                    ..default()
                },
                BackgroundColor(PANEL),
            ))
            .with_children(|p| {
                p.spawn((
                    text("MECHANICS", 28., TEXT),
                    TextLayout::justify(Justify::Center),
                ));
                p.spawn((BodyText, text(stats(body, def, accepted), 16., TEXT)));
                for kind in [Part::Glass, Part::Lamp, Part::Tire] {
                    let present = kind.present(accepted);
                    if present == 0 {
                        continue;
                    }
                    p.spawn(text(kind.title(), 18., TEXT));
                    p.spawn(Node {
                        flex_direction: FlexDirection::Column,
                        row_gap: px(4),
                        ..default()
                    })
                    .with_children(|rows| {
                        // Enumerate canonical accepted bits, not visual max(index)+1:
                        // a missing middle wheel must not create a repairable phantom.
                        for index in (0..32u8).filter(|i| present & (1u32 << i) != 0) {
                            rows.spawn(Node {
                                flex_direction: FlexDirection::Row,
                                column_gap: px(8),
                                ..default()
                            })
                            .with_children(|r| {
                                r.spawn((
                                    PartText(kind, index, present.count_ones() as u8),
                                    text("", 15., TEXT),
                                    TextLayout::new(Justify::Left, LineBreak::NoWrap),
                                    Node {
                                        flex_shrink: 0.,
                                        min_width: px(210),
                                        ..default()
                                    },
                                ));
                                r.spawn((
                                    Button,
                                    Fix(id, kind, index),
                                    Node {
                                        min_width: px(70),
                                        padding: UiRect::all(px(5)),
                                        justify_content: JustifyContent::Center,
                                        ..default()
                                    },
                                    BackgroundColor(BUTTON),
                                ))
                                .with_children(|b| {
                                    b.spawn((
                                        FixText(kind, index),
                                        text(
                                            if kind == Part::Tire { "replace" } else { "fix" },
                                            14.,
                                            TEXT,
                                        ),
                                    ));
                                });
                            });
                        }
                    });
                }
                p.spawn((
                    text("F or Esc to close", 13., Color::srgba(1., 1., 1., 0.5)),
                    TextLayout::justify(Justify::Center),
                ));
            });
        });
}
fn stats(body: &VehicleBody, def: &data::VehicleDefinition, v: &Value) -> String {
    let panes = Part::Glass.present(v);
    let lamps = Part::Lamp.present(v);
    let tires = Part::Tire.present(v);
    format!(
        "{}\n\nengine      {}\nhealth      {:.0} / {:.0}\nfuel        {:.0} / {:.0}\nbattery     {:.0}\ngears       {}\nseats       {}\ntrunk       {}\nglass       {} / {} intact\nlamps       {} / {} working\ntires       {} / {} inflated",
        def.spec.name,
        if mask(v, "flags") & 1 != 0 {
            "running"
        } else {
            "off"
        },
        v["health"].as_f64().unwrap_or(0.),
        def.spec.health * 10.,
        v["fuel"].as_f64().unwrap_or(0.),
        def.spec.fuel,
        v["battery"].as_f64().unwrap_or(0.),
        body.gear_count(),
        def.seats.len(),
        if def.access.iter().any(|z| z.kind == "Trunk") {
            "yes"
        } else {
            "none"
        },
        panes.count_ones() - Part::Glass.broken(v).count_ones(),
        panes.count_ones(),
        lamps.count_ones() - Part::Lamp.broken(v).count_ones(),
        lamps.count_ones(),
        tires.count_ones() - Part::Tire.broken(v).count_ones(),
        tires.count_ones()
    )
}
pub(super) fn refresh(
    mut commands: Commands,
    ui: Res<UiState>,
    menu: Res<Menu>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    bodies: Query<&VehicleBody>,
    mut body_text: Query<&mut Text, (With<BodyText>, Without<PartText>)>,
    mut labels: Query<
        (&PartText, &mut Text, &mut TextColor),
        (Without<BodyText>, Without<FixText>),
    >,
    mut captions: Query<(&FixText, &mut TextColor), Without<PartText>>,
    mut fixes: Query<(
        Entity,
        &Fix,
        &mut BackgroundColor,
        Has<bevy::ui::InteractionDisabled>,
    )>,
    mut wheels: MessageReader<MouseWheel>,
    mut scroll: Query<&mut ScrollPosition, With<Scroll>>,
) {
    if ui.screen != Screen::Mechanics {
        wheels.clear();
        return;
    }
    let Some(id) = menu.mechanics else {
        return;
    };
    let Some(v) = row(&snapshot.0, id) else {
        return;
    };
    let Some(body) = bodies.iter().find(|b| b.net_id == id) else {
        return;
    };
    let Some(def) = data::definition(body.type_id) else {
        return;
    };
    for mut text in &mut body_text {
        **text = stats(body, def, v);
    }
    for (label, mut text, mut color) in &mut labels {
        let dead = label.0.broken(v) & (1 << label.1) != 0;
        **text = format!(
            "{:<width$} {}",
            label.0.display(label.1, label.2, &catalog),
            label.0.status(dead),
            width = if label.0 == Part::Glass { 14 } else { 16 }
        );
        color.0 = if dead {
            Color::srgb(1., 0.55, 0.5)
        } else {
            Color::srgba(1., 1., 1., 0.75)
        };
    }
    for (entity, fix, mut bg, disabled) in &mut fixes {
        let broken = fix.1.broken(v) & (1 << fix.2) != 0;
        bg.0 = if broken {
            BUTTON
        } else {
            Color::srgb(0.12, 0.12, 0.13)
        };
        if broken && disabled {
            commands
                .entity(entity)
                .remove::<bevy::ui::InteractionDisabled>();
        } else if !broken && !disabled {
            commands
                .entity(entity)
                .insert(bevy::ui::InteractionDisabled);
        }
    }
    // Original Godot default Button font .875 RGB; disabled uses alpha .5.
    for (caption, mut color) in &mut captions {
        let broken = caption.0.broken(v) & (1 << caption.1) != 0;
        color.0 = Color::srgba(0.875, 0.875, 0.875, if broken { 1. } else { 0.5 });
    }
    let delta = wheels
        .read()
        .map(|w| {
            w.y * if matches!(w.unit, bevy::input::mouse::MouseScrollUnit::Line) {
                24.
            } else {
                1.
            }
        })
        .sum::<f32>();
    for mut position in &mut scroll {
        position.y = (position.y - delta).max(0.);
    }
}
pub(super) fn repair(
    event: On<Activate>,
    buttons: Query<&Fix>,
    menu: Res<Menu>,
    ui: Res<UiState>,
    snapshot: Res<GameSnapshot>,
    mut intents: MessageWriter<UiIntent>,
) {
    let Ok(fix) = buttons.get(event.entity) else {
        return;
    };
    if ui.screen != Screen::Mechanics || menu.mechanics != Some(fix.0) {
        return;
    }
    if !row(&snapshot.0, fix.0).is_some_and(|v| fix.1.broken(v) & (1 << fix.2) != 0) {
        return;
    }
    if fix.1 == Part::Lamp {
        intent(&mut intents, fix.0, "RepairLamp", fix.2.into());
    } else {
        intents.write(UiIntent::Action{name:"vehicleMaintenance".into(),args:json!({"netId":fix.0,"action":if fix.1==Part::Glass{"RepairGlass"}else{"RepairTire"},"index":fix.2})});
    }
}
