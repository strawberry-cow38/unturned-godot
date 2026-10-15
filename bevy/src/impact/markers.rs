//! Original HitmarkerHUD: four 8px diagonals or a 28-segment damage ring, 0.33 seconds.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) enum Kind {
    Body,
    Head,
    Circle,
}
#[derive(Component)]
pub(super) struct Marker {
    age: f32,
    kind: Kind,
}
#[derive(Component)]
pub(super) struct Segment {
    index: usize,
}
#[derive(Clone, Copy)]
struct Line {
    center: Vec2,
    angle: f32,
    length: f32,
    color: Color,
}
fn line(kind: Kind, age: f32, index: usize) -> Line {
    let t = (age / 0.33).clamp(0., 1.);
    if kind == Kind::Circle {
        let step = std::f32::consts::TAU / 28.;
        let angle = step * (index as f32 + 0.5);
        let radius = 6. + 5. * t;
        Line {
            center: Vec2::from_angle(angle) * radius * (step * 0.5).cos(),
            angle: angle + std::f32::consts::FRAC_PI_2,
            length: 2. * radius * (step * 0.5).sin(),
            color: Color::srgba(1., 1., 1., 0.5 * (1. - t)),
        }
    } else {
        let angle = std::f32::consts::FRAC_PI_4 + index as f32 * std::f32::consts::FRAC_PI_2;
        Line {
            center: Vec2::from_angle(angle) * (5. + 10. * t),
            angle,
            length: 8.,
            color: if kind == Kind::Head {
                Color::srgba(1., 0., 0., 0.5)
            } else {
                Color::srgba(1., 1., 1., 0.5)
            },
        }
    }
}
pub(super) fn spawn(commands: &mut Commands, kind: Kind) {
    let root = commands
        .spawn((
            Name::new("Accepted hitmarker"),
            Marker { age: 0., kind },
            Node {
                position_type: PositionType::Absolute,
                left: percent(50),
                top: percent(50),
                width: px(0),
                height: px(0),
                ..default()
            },
            GlobalZIndex(60),
            Pickable::IGNORE,
        ))
        .id();
    for index in 0..if kind == Kind::Circle { 28 } else { 4 } {
        let l = line(kind, 0., index);
        commands.spawn((
            Segment { index },
            Node {
                position_type: PositionType::Absolute,
                left: px(l.center.x - l.length * 0.5),
                top: px(l.center.y - 1.),
                width: px(l.length),
                height: px(2),
                ..default()
            },
            UiTransform::from_rotation(Rot2::radians(l.angle)),
            BackgroundColor(l.color),
            Pickable::IGNORE,
            ChildOf(root),
        ));
    }
}
pub(super) fn update(
    mut commands: Commands,
    time: Res<Time>,
    ui: Res<UiState>,
    mut marks: Query<(Entity, &mut Marker, &mut Visibility)>,
    mut lines: Query<(
        &Segment,
        &ChildOf,
        &mut Node,
        &mut UiTransform,
        &mut BackgroundColor,
    )>,
) {
    let dt = if crate::player::simulation_paused(&ui) {
        0.
    } else {
        time.delta_secs()
    };
    for (e, mut mark, mut visibility) in &mut marks {
        mark.age += dt;
        *visibility = if ui.screen == crate::ui::Screen::Playing {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if mark.age > 0.33 {
            commands.entity(e).despawn();
        }
    }
    for (s, parent, mut node, mut transform, mut color) in &mut lines {
        let Ok((_, mark, _)) = marks.get(parent.parent()) else {
            continue;
        };
        let l = line(mark.kind, mark.age, s.index);
        node.left = px(l.center.x - l.length * 0.5);
        node.top = px(l.center.y - 1.);
        node.width = px(l.length);
        transform.rotation = Rot2::radians(l.angle);
        color.0 = l.color;
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_marker_geometry_keeps_body_alpha_and_fades_circle() {
        let initial = line(Kind::Body, 0., 0);
        let final_ = line(Kind::Body, 0.33, 0);
        assert!((initial.center.length() - 5.).abs() < 1e-5);
        assert!((final_.center.length() - 15.).abs() < 1e-5);
        assert_eq!(final_.color.alpha(), 0.5);
        assert_eq!(final_.length, 8.);
        let ring = line(Kind::Circle, 0.33, 0);
        assert_eq!(ring.color.alpha(), 0.);
        assert!(ring.length > 2.);
        assert_eq!(line(Kind::Head, 0., 0).color, Color::srgba(1., 0., 0., 0.5));
    }
}
