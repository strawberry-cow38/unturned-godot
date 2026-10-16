//! Original SpinBox arrow and focused-wheel rules. The number itself is local
//! selection state; only dragging its preview emits a SplitItem command.
use super::{gesture::Edge, input::Input};
use bevy::prelude::*;
pub(super) mod capture;

#[derive(Default, Debug)]
pub(super) struct State {
    held: bool,
    remaining: f32,
    pointer: Vec2,
    pub focus_requested: bool,
    first_repeat: bool,
    dragging: bool,
    raw_mode: bool,
    anchor: Vec2,
    base: u16,
    relative_y: f64,
}
pub(super) fn arrows(field: Rect) -> [Rect; 2] {
    // Actual Godot theme enforces height31, with integer heights15 and16.
    let left = field.max.x - 16.;
    [
        Rect::new(left, field.min.y, field.max.x, field.min.y + 15.),
        Rect::new(left, field.min.y + 15., field.max.x, field.max.y),
    ]
}
fn increment(value: &mut u16, maximum: u16, change: i32) {
    *value = (i32::from(*value) + change).clamp(1, i32::from(maximum.max(1))) as u16;
}
impl State {
    #[cfg(test)]
    pub fn captured(&self) -> bool {
        self.dragging && self.raw_mode
    }
    pub fn cancel(&mut self) {
        self.held = false;
        self.focus_requested = false;
        self.dragging = false;
        self.raw_mode = false;
    }
    pub fn input(
        &mut self,
        event: &Input,
        field: Rect,
        offset: Vec2,
        editing: bool,
        value: &mut u16,
        maximum: u16,
    ) -> bool {
        let (edge, p) = match event {
            Input::Cancel => {
                self.cancel();
                return false;
            }
            Input::RelativeMotion(delta) => {
                if self.dragging && self.raw_mode {
                    self.drag(delta.y, value, maximum);
                    return true;
                }
                return false;
            }
            Input::Wheel(p, notches) if editing && field.contains(*p - offset) => {
                increment(value, maximum, notches.round() as i32);
                return true;
            }
            Input::Gesture(edge, p) => (edge, *p - offset),
            _ => return false,
        };
        self.pointer = p;
        let arrow = arrows(field).iter().position(|r| r.contains(p));
        match edge {
            Edge::Left { down: true, .. } if arrow.is_some() => {
                increment(value, maximum, if arrow == Some(0) { 1 } else { -1 });
                self.held = true;
                self.remaining = 0.6;
                self.first_repeat = true;
                self.focus_requested = true;
                self.anchor = p + offset;
                self.dragging = false;
                self.raw_mode = false;
                true
            }
            Edge::Right { down: true, .. } if arrow.is_some() => {
                *value = if arrow == Some(0) { maximum.max(1) } else { 1 };
                self.focus_requested = true;
                true
            }
            Edge::Left { down: false, .. } if self.held => {
                self.cancel();
                true
            }
            Edge::Motion { delta } if self.held => {
                if self.dragging {
                    // Before Winit applies capture, replay logical cursor edges.
                    // Once locked, only raw motion drives the original relative
                    // motion formula; warp-generated CursorMoved is ignored.
                    if !self.raw_mode {
                        self.drag(delta.y, value, maximum);
                    }
                } else if (p + offset).distance(self.anchor) > 2. {
                    self.dragging = true;
                    self.base = *value;
                    self.relative_y = 0.;
                }
                true
            }
            _ => false,
        }
    }
    fn drag(&mut self, dy: f32, value: &mut u16, maximum: u16) {
        if !dy.is_finite() {
            return;
        }
        self.relative_y += f64::from(dy);
        let diff = -0.01 * self.relative_y.abs().powf(1.8) * self.relative_y.signum();
        *value = super::expression::clamp(f64::from(self.base) + diff, maximum);
    }
    pub fn step(&mut self, dt: f32, field: Rect, value: &mut u16, maximum: u16) {
        if !self.held || self.dragging {
            return;
        }
        self.remaining -= dt.max(0.);
        if self.remaining > 0. {
            return;
        }
        // Godot Timer emits once per frame and keeps its remaining period.
        if self.first_repeat {
            self.remaining = 0.075;
            self.first_repeat = false;
        } else {
            self.remaining += 0.075;
        }
        if let Some(arrow) = arrows(field).iter().position(|r| r.contains(self.pointer)) {
            increment(value, maximum, if arrow == 0 { 1 } else { -1 });
        }
    }
    pub fn pressed(&self, index: usize, field: Rect) -> bool {
        self.held && arrows(field)[index].contains(self.pointer)
    }
    pub fn hovered(&self, index: usize, field: Rect) -> bool {
        arrows(field)[index].contains(self.pointer)
    }
}
pub(super) fn committed(text: &str, old: u16, maximum: u16) -> u16 {
    super::expression::committed(text, old, maximum)
}

#[derive(Component)]
pub(super) struct Arrow(usize);
pub(super) fn draw(
    p: &mut ChildSpawnerCommands,
    field: Rect,
    menu: &super::controller::Menu,
    art: &mut super::tiles::Art,
    images: &mut Assets<Image>,
) {
    use bevy::{
        asset::RenderAssetUsages,
        image::{CompressedImageFormats, ImageSampler, ImageType},
        picking::Pickable,
    };
    let handles = art
        .spin
        .get_or_insert_with(|| {
            [
                include_bytes!("../../../assets/ui/spin_increment.png").as_slice(),
                include_bytes!("../../../assets/ui/spin_decrement.png").as_slice(),
            ]
            .map(|bytes| {
                images.add(
                    Image::from_buffer(
                        bytes,
                        ImageType::Extension("png"),
                        CompressedImageFormats::NONE,
                        true,
                        ImageSampler::linear(),
                        RenderAssetUsages::default(),
                    )
                    .unwrap(),
                )
            })
        })
        .clone();
    let maximum = menu
        .gesture
        .selected
        .and_then(|cell| menu.view.tile(cell))
        .map_or(1, |tile| tile.amount.saturating_sub(1));
    for (index, r) in arrows(field).into_iter().enumerate() {
        let disabled = if index == 0 {
            menu.gesture.split_amount >= maximum
        } else {
            menu.gesture.split_amount <= 1
        };
        p.spawn((
            super::tiles::rect(r),
            Arrow(index),
            BackgroundColor(Color::NONE),
            Pickable::IGNORE,
        ))
        .with_child((
            super::tiles::at(0., if index == 0 { 3. } else { 4. }, 16., 8.),
            ImageNode {
                image: handles[index].clone(),
                color: Color::srgba(0.875, 0.875, 0.875, if disabled { 0.5 } else { 1. }),
                ..default()
            },
            Pickable::IGNORE,
        ));
    }
}
pub(super) fn appearance(
    menu: Res<super::controller::Menu>,
    mut arrows: Query<(&Arrow, &Children, &mut BackgroundColor)>,
    mut images: Query<&mut ImageNode>,
) {
    let Some(field) = menu.hits.number else {
        return;
    };
    let maximum = menu
        .gesture
        .selected
        .and_then(|cell| menu.view.tile(cell))
        .map_or(1, |tile| tile.amount.saturating_sub(1));
    for (arrow, children, mut bg) in &mut arrows {
        let disabled = if arrow.0 == 0 {
            menu.gesture.split_amount >= maximum
        } else {
            menu.gesture.split_amount <= 1
        };
        let pressed = menu.spin.pressed(arrow.0, field);
        let hover = menu.spin.hovered(arrow.0, field);
        bg.0 = if disabled {
            Color::NONE
        } else if pressed {
            Color::srgba(0., 0., 0., 0.6)
        } else if hover {
            Color::srgba(0.225, 0.225, 0.225, 0.6)
        } else {
            Color::NONE
        };
        for child in children {
            if let Ok(mut image) = images.get_mut(*child) {
                let light = if !disabled && (hover || pressed) {
                    0.95
                } else {
                    0.875
                };
                image.color = Color::srgba(light, light, light, if disabled { 0.5 } else { 1. });
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_arrow_tap_hold_right_extremes_and_editing_wheel_keep_no_gameplay_mutation() {
        let field = Rect::new(100., 200., 200., 231.);
        let up = arrows(field)[0].center();
        let mut s = State::default();
        let mut value = 4;
        assert!(s.input(
            &Input::Gesture(
                Edge::Left {
                    down: true,
                    control: false
                },
                up
            ),
            field,
            Vec2::ZERO,
            false,
            &mut value,
            20
        ));
        assert_eq!(value, 5);
        s.step(0.59, field, &mut value, 20);
        assert_eq!(value, 5);
        s.step(0.02, field, &mut value, 20);
        assert_eq!(value, 6);
        s.input(
            &Input::Gesture(
                Edge::Left {
                    down: false,
                    control: false,
                },
                up,
            ),
            field,
            Vec2::ZERO,
            false,
            &mut value,
            20,
        );
        s.step(5., field, &mut value, 20);
        assert_eq!(value, 6);
        s.input(
            &Input::Gesture(
                Edge::Right {
                    down: true,
                    control: false,
                },
                up,
            ),
            field,
            Vec2::ZERO,
            false,
            &mut value,
            20,
        );
        assert_eq!(value, 20);
        assert!(!s.input(
            &Input::Wheel(field.center(), -1.),
            field,
            Vec2::ZERO,
            false,
            &mut value,
            20
        ));
        assert!(s.input(
            &Input::Wheel(field.center(), -1.),
            field,
            Vec2::ZERO,
            true,
            &mut value,
            20
        ));
        assert_eq!(value, 19);
        assert_eq!(committed("", 7, 20), 7);
        assert_eq!(committed("NaN", 7, 20), 7);
        assert_eq!(committed("22", 7, 20), 20);
        assert_eq!(committed("2.5", 7, 20), 3);
    }
}

#[cfg(test)]
mod capture_tests;

#[cfg(test)]
mod number_tests;
