//! Original VScrollBar range/drag semantics with the runtime Godot default theme.
//! Theme probe: empty arrow icons; track marginsY0; grabber minimum8px.
use super::{controller::Menu, gesture::Edge, input::Input, layout, tiles};
use bevy::{picking::Pickable, prelude::*};
#[derive(Clone, Copy, Debug)]
pub(super) struct Drag {
    column: usize,
    at: f32,
    value: f32,
}
#[derive(Clone, Copy, Debug)]
pub(super) struct Geometry {
    pub track: Rect,
    pub thumb: Rect,
    area: f32,
    total: f32,
    page: f32,
}
pub(super) fn geometry(
    d: &layout::Dashboard,
    scroll: &layout::Scroll,
    column: usize,
) -> Option<Geometry> {
    let total = scroll.content[column];
    let page = d.storage.height();
    if total <= page || page <= 8. {
        return None;
    }
    let x = if column == 0 {
        d.storage.min.x
    } else {
        d.container_x
    } + d.column_width.min(8. * super::model::CELL)
        + 8.;
    let track = Rect::new(x, d.storage.min.y, x + 14., d.storage.max.y);
    let area = track.height() - 8.;
    let y = track.min.y + area * scroll.offset[column] / total;
    Some(Geometry {
        track,
        thumb: Rect::new(x, y, x + 14., y + 8. + area * page / total),
        area,
        total,
        page,
    })
}
fn set(menu: &mut Menu, col: usize, value: f32, d: &layout::Dashboard) {
    // Original Step=10; Range.set_value snaps then clamps to max-page.
    let max = (menu.scroll.content[col] - d.storage.height()).max(0.);
    let value = ((value / 10. + 0.5).floor() * 10.).clamp(0., max);
    if menu.scroll.offset[col] != value {
        menu.scroll.offset[col] = value;
        menu.dirty();
    }
}
pub(super) fn input(menu: &mut Menu, event: &Input, offset: Vec2, d: &layout::Dashboard) -> bool {
    if matches!(event, Input::Cancel) {
        menu.scrolling = None;
        return false;
    }
    let Input::Gesture(edge, p) = event else {
        return false;
    };
    let point = *p - offset;
    if let Some(drag) = menu.scrolling {
        match edge {
            Edge::Motion { .. } => {
                if let Some(g) = geometry(d, &menu.scroll, drag.column) {
                    set(
                        menu,
                        drag.column,
                        drag.value + (point.y - drag.at) / g.area * g.total,
                        d,
                    );
                }
            }
            Edge::Left { down: false, .. } => menu.scrolling = None,
            _ => {}
        }
        return true;
    }
    if menu.gesture.selected.is_some() {
        return false;
    }
    if matches!(edge, Edge::Left { down: true, .. }) {
        for column in 0..=usize::from(d.split) {
            if let Some(g) = geometry(d, &menu.scroll, column).filter(|g| g.track.contains(point)) {
                if g.thumb.contains(point) {
                    menu.scrolling = Some(Drag {
                        column,
                        at: point.y,
                        value: menu.scroll.offset[column],
                    });
                } else {
                    set(
                        menu,
                        column,
                        menu.scroll.offset[column]
                            + if point.y < g.thumb.min.y {
                                -g.page
                            } else {
                                g.page
                            },
                        d,
                    );
                }
                return true;
            }
        }
    }
    false
}
#[derive(Component)]
pub(super) struct Thumb(usize);
pub(super) fn appearance(menu: Res<Menu>, mut thumbs: Query<(&Thumb, &mut BackgroundColor)>) {
    let d = layout::Dashboard::new(menu.viewport);
    for (thumb, mut color) in &mut thumbs {
        if let Some(g) = geometry(&d, &menu.scroll, thumb.0) {
            color.0 = if menu.scrolling.is_some_and(|s| s.column == thumb.0) {
                Color::srgba(0.75, 0.75, 0.75, 0.75)
            } else if g.track.contains(menu.gesture.pointer) {
                Color::srgba(1., 1., 1., 0.75)
            } else {
                Color::srgba(1., 1., 1., 0.4)
            };
        }
    }
}
pub(super) fn draw(p: &mut ChildSpawnerCommands, menu: &mut Menu, d: &layout::Dashboard) {
    for column in 0..=usize::from(d.split) {
        let Some(g) = geometry(d, &menu.scroll, column) else {
            continue;
        };
        menu.hits.buttons.push(g.track);
        let mut track = tiles::rect(g.track);
        track.border_radius = BorderRadius::all(px(10));
        p.spawn((
            track,
            BackgroundColor(Color::srgba(0.1, 0.1, 0.1, 0.6)),
            Pickable::IGNORE,
        ));
        let mut thumb = tiles::rect(g.thumb);
        thumb.border_radius = BorderRadius::all(px(10));
        let color = if menu.scrolling.is_some_and(|s| s.column == column) {
            Color::srgba(0.75, 0.75, 0.75, 0.75)
        } else if g.track.contains(menu.gesture.pointer) {
            Color::srgba(1., 1., 1., 0.75)
        } else {
            Color::srgba(1., 1., 1., 0.4)
        };
        p.spawn((
            thumb,
            Thumb(column),
            BackgroundColor(color),
            Pickable::IGNORE,
        ));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_scrollbar_page_thumb_drag_and_source_ten_pixel_snap() {
        let d = layout::Dashboard::new(Vec2::new(1600., 900.));
        let mut menu = Menu::default();
        menu.scroll.content = [d.storage.height() * 3., 0.];
        let g = geometry(&d, &menu.scroll, 0).unwrap();
        assert_eq!(g.track.width(), 14.);
        assert!((g.thumb.height() - (8. + g.area / 3.)).abs() < 0.001);
        let at = g.thumb.center();
        assert!(input(
            &mut menu,
            &Input::Gesture(
                Edge::Left {
                    down: true,
                    control: false
                },
                at
            ),
            Vec2::ZERO,
            &d
        ));
        assert!(input(
            &mut menu,
            &Input::Gesture(
                Edge::Motion { delta: Vec2::ZERO },
                at + Vec2::Y * g.area / 3.
            ),
            Vec2::ZERO,
            &d
        ));
        assert_eq!(
            menu.scroll.offset[0],
            (d.storage.height() / 10. + 0.5).floor() * 10.
        );
        input(
            &mut menu,
            &Input::Gesture(
                Edge::Left {
                    down: false,
                    control: false,
                },
                at,
            ),
            Vec2::ZERO,
            &d,
        );
        assert!(menu.scrolling.is_none());
        assert_eq!(menu.scroll.offset[1], 0.);
        assert!(geometry(&d, &menu.scroll, 1).is_none());
    }
}
