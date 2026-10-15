//! The original bonnet panel. Persistent rows read accepted facts every frame;
//! a click requests a free lamp repair and never edits the native lens mask.
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
#[derive(Component)]
pub(super) struct LampText(u8);
#[derive(Component)]
pub(super) struct TireText(usize, usize);
#[derive(Component)]
pub(super) struct Fix(u32, u8);
#[derive(Component)]
pub(super) struct FixText(u8);
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
    // Use the binding itself so closing a rebound F or mouse action consumes
    // exactly that edge, without clearing unrelated held movement keys.
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
fn mask(v: &Value, key: &str) -> u8 {
    v[key].as_u64().unwrap_or(0) as u8
}

pub(super) fn build(
    mut commands: Commands,
    ui: Res<UiState>,
    menu: Res<Menu>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    roots: Query<Entity, With<Root>>,
    bodies: Query<&VehicleBody>,
    tires: Query<&crate::vehicle::TireVisual>,
    mut previous: Local<Option<(u64, u32, u8, usize)>>,
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
    let tire_count = tires
        .iter()
        .filter(|t| bodies.get(t.body).is_ok_and(|b| b.net_id == id))
        .map(|t| t.index + 1)
        .max()
        .unwrap_or(0);
    let lamp_mask = mask(accepted, "lampMask");
    let key = (menu.epoch, id, lamp_mask, tire_count);
    if *previous == Some(key) && !roots.is_empty() {
        return;
    }
    *previous = Some(key);
    for e in &roots {
        commands.entity(e).despawn();
    }
    let labels = catalog
        .vehicle_lighting
        .as_ref()
        .map(|d| d.lamp_labels.clone())
        .unwrap_or_default();
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
                p.spawn((
                    BodyText,
                    text(stats(body, def, accepted, tire_count), 16., TEXT),
                ));
                if lamp_mask != 0 {
                    p.spawn(text("LAMPS", 18., TEXT));
                }
                if lamp_mask != 0 {
                    p.spawn(Node {
                        flex_direction: FlexDirection::Column,
                        row_gap: px(4),
                        ..default()
                    })
                    .with_children(|rows| {
                        for (i, label) in labels
                            .iter()
                            .enumerate()
                            .filter(|(i, _)| lamp_mask & (1 << i) != 0)
                        {
                            rows.spawn(Node {
                                flex_direction: FlexDirection::Row,
                                column_gap: px(8),
                                ..default()
                            })
                            .with_children(|r| {
                                r.spawn((
                                    LampText(i as u8),
                                    text(lamp_display(label), 15., TEXT),
                                    TextLayout::new(Justify::Left, LineBreak::NoWrap),
                                    Node {
                                        flex_shrink: 0.,
                                        min_width: px(210),
                                        ..default()
                                    },
                                ));
                                r.spawn((
                                    Button,
                                    Fix(id, i as u8),
                                    Node {
                                        min_width: px(70),
                                        padding: UiRect::all(px(5)),
                                        justify_content: JustifyContent::Center,
                                        ..default()
                                    },
                                    BackgroundColor(BUTTON),
                                ))
                                .with_children(|b| {
                                    b.spawn((FixText(i as u8), text("fix", 14., TEXT)));
                                });
                            });
                        }
                    });
                }
                if tire_count > 0 {
                    p.spawn(text("TIRES", 18., TEXT));
                    p.spawn(Node {
                        flex_direction: FlexDirection::Column,
                        row_gap: px(4),
                        ..default()
                    })
                    .with_children(|rows| {
                        for i in 0..tire_count {
                            rows.spawn((
                                TireText(i, tire_count),
                                text("", 15., TEXT),
                                TextLayout::new(Justify::Left, LineBreak::NoWrap),
                            ));
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
fn stats(
    body: &VehicleBody,
    def: &data::VehicleDefinition,
    accepted: &Value,
    tires: usize,
) -> String {
    let lamp = mask(accepted, "lampMask");
    let broken = mask(accepted, "brokenLampMask") & lamp;
    let flat = (mask(accepted, "poppedTireMask") as u32 & ((1u32 << tires.min(8)) - 1)).count_ones()
        as usize;
    format!(
        "{}\n\nengine      {}\nhealth      {:.0} / {:.0}\nfuel        {:.0} / {:.0}\nbattery     {:.0}\ngears       {}\nseats       {}\ntrunk       {}\nlamps       {} / {} working\ntires       {} / {} inflated",
        def.spec.name,
        if mask(accepted, "flags") & 1 != 0 {
            "running"
        } else {
            "off"
        },
        accepted["health"].as_f64().unwrap_or(0.),
        def.spec.health * 10.,
        accepted["fuel"].as_f64().unwrap_or(0.),
        def.spec.fuel,
        accepted["battery"].as_f64().unwrap_or(0.),
        body.gear_count(),
        def.seats.len(),
        if def.access.iter().any(|z| z.kind == "Trunk") {
            "yes"
        } else {
            "none"
        },
        lamp.count_ones() - broken.count_ones(),
        lamp.count_ones(),
        tires - flat,
        tires
    )
}
pub(super) fn refresh(
    mut commands: Commands,
    ui: Res<UiState>,
    menu: Res<Menu>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    bodies: Query<&VehicleBody>,
    tires: Query<&crate::vehicle::TireVisual>,
    mut body_text: Query<&mut Text, (With<BodyText>, Without<LampText>, Without<TireText>)>,
    mut lamps: Query<
        (&LampText, &mut Text, &mut TextColor),
        (Without<BodyText>, Without<TireText>, Without<FixText>),
    >,
    mut tire_labels: Query<
        (&TireText, &mut Text, &mut TextColor),
        (Without<BodyText>, Without<LampText>, Without<FixText>),
    >,
    mut captions: Query<(&FixText, &mut TextColor), (Without<LampText>, Without<TireText>)>,
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
    let total = tires
        .iter()
        .filter(|t| bodies.get(t.body).is_ok_and(|b| b.net_id == id))
        .map(|t| t.index + 1)
        .max()
        .unwrap_or(0);
    for mut text in &mut body_text {
        **text = stats(body, def, v, total);
    }
    for (label, mut text, mut color) in &mut lamps {
        let dead = mask(v, "brokenLampMask") & (1 << label.0) != 0;
        let name = catalog
            .vehicle_lighting
            .as_ref()
            .and_then(|d| d.lamp_labels.get(label.0 as usize))
            .map(String::as_str)
            .unwrap_or("");
        **text = format!(
            "{:<16} {}",
            lamp_display(name),
            if dead { "SHOT OUT" } else { "working" }
        );
        color.0 = if dead {
            Color::srgb(1., 0.55, 0.5)
        } else {
            Color::srgba(1., 1., 1., 0.75)
        };
    }
    for (label, mut text, mut color) in &mut tire_labels {
        let flat = mask(v, "poppedTireMask") & (1 << label.0) != 0;
        **text = format!(
            "{:<16} {}",
            tire_display(label.0, label.1),
            if flat { "BLOWN" } else { "ok" }
        );
        color.0 = if flat {
            Color::srgb(1., 0.55, 0.5)
        } else {
            Color::srgba(1., 1., 1., 0.75)
        };
    }
    for (entity, fix, mut bg, disabled) in &mut fixes {
        let broken = mask(v, "brokenLampMask") & (1 << fix.1) != 0;
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
    // MechanicsPanel uses the engine's default Button theme. Godot4.6:
    // font_color=.875 RGB, disabled font uses the same RGB at alpha .5.
    for (caption, mut color) in &mut captions {
        let broken = mask(v, "brokenLampMask") & (1 << caption.0) != 0;
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
    if row(&snapshot.0, fix.0).is_some_and(|v| mask(v, "brokenLampMask") & (1 << fix.1) != 0) {
        intent(&mut intents, fix.0, "RepairLamp", fix.1.into());
    }
}
