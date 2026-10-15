//! PlayerController.StepLean/ApplyLean: 50 Hz side/cover decision, rendered
//! 4/s pivot easing. The pivot is at the feet, so leaning actually peeks.
use super::*;

pub(crate) const DEGREES: f32 = 20.;
const REACH: f32 = 0.95;
const RADIUS: f32 = 0.3;
const TAP_WINDOW: f32 = 0.075;

#[derive(Resource)]
pub(crate) struct LeanState {
    /// Source positive roll leans left. HDR rolls the composited image by the
    /// negative angle; never rotate the 3D arms or their ADS measurement.
    pub angle: f32,
    pub eye_height: f32,
    side: i8,
    obstructed: bool,
    held: [bool; 2],
    input_age: f32,
    epoch: u64,
}
impl Default for LeanState {
    fn default() -> Self {
        Self {
            angle: 0.,
            eye_height: 1.6,
            side: 0,
            obstructed: false,
            held: [false; 2],
            input_age: 0.,
            epoch: 0,
        }
    }
}
impl LeanState {
    pub(crate) fn eyes(&self, feet: Vec3, yaw: f32) -> Vec3 {
        feet + pivot(yaw, self.angle) * Vec3::Y * self.eye_height
    }
    pub(crate) fn aim(&self, yaw: f32, pitch: f32) -> Quat {
        pivot(yaw, self.angle) * Quat::from_rotation_x(pitch.to_radians())
    }
    fn keys(&mut self, held: [bool; 2], first_person: bool, dt: f32) -> ([bool; 2], Option<bool>) {
        self.input_age += dt;
        let mut left = None;
        if held[0] && !self.held[0] {
            left = Some(true);
            self.input_age = 0.;
        }
        if held[1] && !self.held[1] {
            left = Some(false);
            self.input_age = 0.;
        }
        self.held = held;
        (
            if !first_person && self.input_age <= TAP_WINDOW {
                [false; 2]
            } else {
                held
            },
            left,
        )
    }
}
pub(crate) fn pivot(yaw: f32, angle: f32) -> Quat {
    Quat::from_rotation_y(yaw.to_radians()) * Quat::from_rotation_z(angle)
}
fn choose(left: bool, right: bool, stance: &str, clear: bool) -> (i8, bool) {
    if matches!(stance, "climb" | "sprint" | "driving" | "sitting") || left == right {
        (0, false)
    } else if !clear {
        (0, true)
    } else {
        (if left { 1 } else { -1 }, false)
    }
}
fn struggle(previous: i8, next: i8, arrested: bool) -> Option<u8> {
    (arrested && next != 0 && next != previous).then_some((next > 0) as u8)
}
/// Full capsule extent is [eye, eye+direction*.95], including its caps. A
/// shoulder flush against the opposite wall must not reject leaning away.
fn space_empty(context: &RapierContext, owner: Entity, eyes: Vec3, direction: Vec3) -> bool {
    let shape = Collider::capsule_y(REACH * 0.5 - RADIUS, RADIUS);
    let mut blocked = false;
    context.intersect_shape(
        eyes + direction * REACH * 0.5,
        Quat::from_rotation_arc(Vec3::Y, direction),
        &shape,
        QueryFilter::default()
            .exclude_collider(owner)
            .exclude_sensors()
            .groups(CollisionGroups::new(
                Group::ALL,
                Group::GROUP_1 | Group::GROUP_4,
            )),
        |_, _| {
            blocked = true;
            false
        },
    );
    !blocked
}
pub(super) fn step(
    time: Res<Time<Fixed>>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    input: Res<PlayerInput>,
    authority: (Option<Res<CoreBridge>>, Option<Res<GameSnapshot>>),
    occupancy: Res<crate::vehicle::Occupancy>,
    prop: Res<crate::world::interactables::PropOccupancy>,
    context: ReadRapierContext,
    bodies: Query<(Entity, &Transform, &PlayerBody)>,
    mut view: ResMut<crate::avatar::Perspective>,
    mut lean: ResMut<LeanState>,
) {
    if simulation_paused(&ui) {
        return;
    }
    let blocked = ui.captures_input()
        || !model.connected
        || !model.alive
        || occupancy.seat.is_some()
        || prop.seat.is_some();
    let held = [GameAction::LeanLeft, GameAction::LeanRight]
        .map(|action| !blocked && settings.pressed(action, &keys, &mouse));
    let (held, side) = lean.keys(held, view.first_person, time.delta_secs());
    if let Some(left) = side {
        view.left = left;
    }
    let Ok((entity, transform, body)) = bodies.single() else {
        lean.side = 0;
        return;
    };
    let clear = held[0] == held[1]
        || context.single().ok().is_none_or(|context| {
            let right = Quat::from_rotation_y(input.yaw.to_radians()) * Vec3::X;
            space_empty(
                &context,
                entity,
                transform.translation + Vec3::Y * body.eye_height,
                if held[0] { -right } else { right },
            )
        });
    let previous = lean.side;
    (lean.side, lean.obstructed) = choose(held[0], held[1], &body.stance, clear);
    if let Some(side) = struggle(
        previous,
        lean.side,
        authority
            .1
            .as_ref()
            .is_some_and(|s| s.0["player"]["arrested"].as_bool() == Some(true)),
    ) {
        if let Some(bridge) = authority.0 {
            if let Err(error) =
                bridge.request("action", json!({"name":"struggle","args":{"side":side}}))
            {
                warn!("Lean restraint struggle: {error}");
            }
        }
    }
}
pub(super) fn ease(
    time: Res<Time>,
    ui: Res<UiState>,
    epoch: Res<WorldSessionEpoch>,
    bodies: Query<&PlayerBody>,
    mut lean: ResMut<LeanState>,
) {
    if lean.epoch != epoch.0 {
        *lean = LeanState {
            epoch: epoch.0,
            ..default()
        };
    }
    if simulation_paused(&ui) {
        return;
    }
    let k = (4. * time.delta_secs()).min(1.);
    // The selected Godot port deliberately eases obstruction back to center;
    // retail's immediate snap is not the behavior being ported here.
    let target = if lean.obstructed {
        0.
    } else {
        lean.side as f32 * DEGREES.to_radians()
    };
    lean.angle += (target - lean.angle) * k;
    if let Ok(body) = bodies.single() {
        lean.eye_height += (body.eye_height - lean.eye_height) * k;
    }
}

#[cfg(test)]
mod tests;
