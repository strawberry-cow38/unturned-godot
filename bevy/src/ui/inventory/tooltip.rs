//! InventoryUI uses Godot's normal Control tooltip for quick craft and the
//! action yield. Ordinary item tiles deliberately have no tooltip.
use super::controller::Menu;
use bevy::{picking::Pickable, prelude::*};

#[derive(Resource, Default)]
pub(super) struct Hover {
    target: Option<(Rect, String)>,
    elapsed: f32,
    position: Vec2,
}
#[derive(Component)]
pub(super) struct Panel;
#[derive(Component)]
pub(super) struct Caption;
impl Hover {
    fn step(&mut self, target: Option<(Rect, String)>, position: Vec2, delta: f32) -> bool {
        if self.target != target
            || (self.elapsed < 0.5 && self.position.distance_squared(position) > 25.)
        {
            self.target = target;
            self.position = position;
            self.elapsed = 0.;
        } else {
            self.elapsed += delta.max(0.);
        }
        self.target.is_some() && self.elapsed >= 0.5
    }
}
pub(super) fn draw(p: &mut ChildSpawnerCommands, font: &Handle<Font>) {
    p.spawn((
        Panel,
        Node {
            position_type: PositionType::Absolute,
            padding: UiRect::axes(px(8), px(2)),
            border_radius: BorderRadius::all(px(3)),
            ..default()
        },
        GlobalZIndex(90),
        BackgroundColor(Color::srgba(0., 0., 0., 0.5)),
        Visibility::Hidden,
        Pickable::IGNORE,
    ))
    .with_child((
        Caption,
        Text::new(""),
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
pub(super) fn update(
    menu: Res<Menu>,
    time: Res<Time>,
    frame: Res<crate::ui::input_order::FrameInput>,
    windows: Query<Entity, With<bevy::window::PrimaryWindow>>,
    mut hover: ResMut<Hover>,
    mut panels: Query<(&mut Node, &ComputedNode, &mut Visibility), With<Panel>>,
    mut text: Query<&mut Text, With<Caption>>,
) {
    let point = windows
        .single()
        .ok()
        .and_then(|w| frame.pointer(w))
        .map_or(menu.gesture.pointer, |p| p - menu.offset);
    let target = (menu.open && menu.gesture.carry.is_none())
        .then(|| {
            menu.hits
                .tooltips
                .iter()
                .find(|(rect, _)| rect.contains(point))
                .cloned()
        })
        .flatten();
    let visible = hover.step(target, point, time.delta_secs());
    for (mut node, computed, mut visibility) in &mut panels {
        *visibility = if visible {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        // Source default tooltip offset; keep the popup inside the viewport.
        let size = computed.size() * computed.inverse_scale_factor();
        let mut position = hover.position + Vec2::splat(10.);
        for axis in 0..2 {
            if position[axis] + size[axis] > menu.viewport[axis] {
                position[axis] = hover.position[axis] - size[axis] - 10.;
                if position[axis] < 0. {
                    position[axis] = menu.viewport[axis] - size[axis];
                }
            } else if position[axis] < 0. {
                position[axis] = 0.;
            }
        }
        node.left = px(position.x);
        node.top = px(position.y);
    }
    for mut text in &mut text {
        text.0 = hover
            .target
            .as_ref()
            .map_or("", |(_, title)| title.as_str())
            .into();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_tooltip_waits_for_stationary_hover_and_resets_on_leave_or_new_action() {
        let mut hover = Hover::default();
        let target = Some((
            Rect::new(0., 0., 72., 72.),
            "Planks\nLMB +1   RMB +5".into(),
        ));
        assert!(!hover.step(target.clone(), Vec2::ONE, 1.));
        assert!(!hover.step(target.clone(), Vec2::ONE, 0.49));
        assert!(hover.step(target.clone(), Vec2::ONE, 0.011));
        assert!(
            hover.step(target.clone(), Vec2::splat(20.), 0.02),
            "shown tooltip remains anchored while its control stays hovered"
        );
        assert!(!hover.step(None, Vec2::splat(100.), 1.));
        assert!(!hover.step(target, Vec2::ONE, 1.));
    }
}
