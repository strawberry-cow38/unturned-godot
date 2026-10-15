//! Original ShowHurtCosmetics / HurtDirectionIndicator presentation. Only an
//! accepted owner hurt event makes a flash/mark; a health snapshot never does.
use super::{UiModel, UiState};
use crate::{
    entities::CoreEvent,
    player::{PlayerBody, PlayerCamera, WorldSessionEpoch},
};
use bevy::{
    asset::embedded_asset, prelude::*, render::render_resource::AsBindGroup, shader::ShaderRef,
};

const MARK_TIME: f32 = 3.;
const FADE_START: f32 = 1.5;

#[derive(Clone, Copy, Debug)]
struct Mark {
    sequence: u64,
    /// Victim -> attacker, flattened once at the accepted hit. Keeping a
    /// direction instead of a point also survives floating-origin translation.
    direction: Vec3,
    age: f32,
    critical: bool,
}
impl Mark {
    fn alpha(self) -> f32 {
        if self.age < FADE_START {
            1.
        } else {
            (1. - (self.age - FADE_START) / (MARK_TIME - FADE_START)).max(0.)
        }
    }
}

#[derive(Resource)]
pub(crate) struct HurtFeedback {
    /// Original HUD.CR tint is composited before LowHealthOverlay by its pass.
    pub(crate) pain_alpha: f32,
    marks: Vec<Mark>,
    epoch: u64,
    last_event: u64,
    was_alive: bool,
}
impl Default for HurtFeedback {
    fn default() -> Self {
        Self {
            pain_alpha: 0.,
            marks: vec![],
            epoch: u64::MAX,
            last_event: 0,
            was_alive: false,
        }
    }
}
impl HurtFeedback {
    fn hit(&mut self, sequence: u64, damage: f32, feet: Option<Vec3>, source: Option<Vec3>) {
        if !damage.is_finite() || damage <= 0. {
            return;
        }
        // Source assigns, rather than max/accumulate: a later weaker hit may
        // replace a stronger flash. Small damage still has a directional mark.
        if damage > 5. {
            self.pain_alpha = (damage / 40.).clamp(0., 1.) * 0.75;
        }
        if let Some(direction) = feet
            .zip(source)
            .map(|(feet, source)| (source - feet).with_y(0.))
        {
            if direction.is_finite() && direction.length_squared() > 0.0001 {
                self.marks.push(Mark {
                    sequence,
                    direction: direction.normalize(),
                    age: 0.,
                    critical: damage >= 25.,
                });
            }
        }
    }
    fn advance(&mut self, dt: f32) {
        self.pain_alpha = (self.pain_alpha - dt).max(0.);
        for mark in &mut self.marks {
            mark.age += dt;
        }
        self.marks.retain(|mark| mark.age < MARK_TIME);
    }
}

#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct HurtUpdate;
pub(super) struct HurtPlugin;
impl Plugin for HurtPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "hurt/wedge.wesl");
        app.init_resource::<HurtFeedback>()
            .add_plugins(UiMaterialPlugin::<WedgeMaterial>::default())
            .add_systems(
                Update,
                receive
                    .in_set(HurtUpdate)
                    .after(crate::entities::CoreEventsReady)
                    .after(crate::player::PlayerCameraUpdate),
            )
            .add_systems(PostUpdate, draw.before(bevy::ui::UiSystems::Layout));
    }
}

fn receive(
    mut feedback: ResMut<HurtFeedback>,
    mut events: MessageReader<CoreEvent>,
    epoch: Res<WorldSessionEpoch>,
    model: Res<UiModel>,
    ui: Res<UiState>,
    time: Res<Time>,
    bodies: Query<&Transform, With<PlayerBody>>,
) {
    let changed = feedback.epoch != epoch.0;
    let revived = !feedback.was_alive && model.alive;
    if changed || !model.connected {
        feedback.pain_alpha = 0.;
        feedback.marks.clear();
        if changed {
            feedback.last_event = 0;
        }
    }
    if revived {
        // Original respawn resets PainAlpha; the independent mark control
        // continues its ordinary lifetime instead of replaying old hits.
        feedback.pain_alpha = 0.;
    }
    feedback.epoch = epoch.0;
    feedback.was_alive = model.alive;
    let paused = crate::player::simulation_paused(&ui);
    let feet = bodies.single().ok().map(|body| body.translation);
    for event in events.read() {
        if event.sequence <= feedback.last_event {
            continue;
        }
        feedback.last_event = event.sequence;
        if !model.connected || changed || paused || event.kind != "hurt" {
            continue;
        }
        if let Some(damage) = event.data["damage"].as_f64() {
            let source = (event.data["hasSource"].as_bool() == Some(true))
                .then(|| crate::player::vector(&event.data["sourcePos"]))
                .flatten();
            feedback.hit(event.sequence, damage as f32, feet, source);
        }
    }
    // A lethal accepted hit is still visible beneath the death menu. Source
    // ProcessTick and the mark's TickHub continue aging until respawn clears it.
    if !paused {
        feedback.advance(time.delta_secs());
    }
}

/// Same flattened camera basis as HurtDirectionIndicator._Draw; pitch alone
/// cannot rotate a mark, while vehicle roll and free look use the actual eye.
fn bearing(direction: Vec3, camera: &Transform) -> Option<f32> {
    let forward = (*camera.forward()).with_y(0.);
    let right = (*camera.right()).with_y(0.);
    if !forward.is_finite()
        || !right.is_finite()
        || forward.length_squared() < 0.0001
        || right.length_squared() < 0.0001
    {
        return None;
    }
    Some(
        direction
            .dot(right.normalize())
            .atan2(direction.dot(forward.normalize())),
    )
}

#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
struct WedgeMaterial {
    /// Bearing radians, lifetime alpha, critical flag, reserved.
    #[uniform(0)]
    state: Vec4,
}
impl UiMaterial for WedgeMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/ui/hurt/wedge.wesl".into()
    }
}
#[derive(Component)]
struct Wedge(u64);

fn draw(
    mut commands: Commands,
    feedback: Res<HurtFeedback>,
    model: Res<UiModel>,
    ui: Res<UiState>,
    camera: Query<&Transform, With<PlayerCamera>>,
    mut materials: ResMut<Assets<WedgeMaterial>>,
    mut wedges: Query<(
        Entity,
        &Wedge,
        &MaterialNode<WedgeMaterial>,
        &mut Visibility,
    )>,
) {
    let visible = model.connected && !ui.menu_camera();
    let camera = camera.single().ok();
    let mut existing = std::collections::HashSet::new();
    for (entity, wedge, material, mut visibility) in &mut wedges {
        let mark = feedback.marks.iter().find(|mark| mark.sequence == wedge.0);
        let Some(mark) = mark else {
            materials.remove(material.0.id());
            commands.entity(entity).despawn();
            continue;
        };
        existing.insert(wedge.0);
        let angle = camera.and_then(|camera| bearing(mark.direction, camera));
        *visibility = if visible && angle.is_some() {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if let Some(mut material) = materials.get_mut(&material.0) {
            material.state = Vec4::new(
                angle.unwrap_or(0.),
                mark.alpha(),
                mark.critical as u8 as f32,
                0.,
            );
        }
    }
    for mark in &feedback.marks {
        if existing.contains(&mark.sequence) {
            continue;
        }
        let angle = camera.and_then(|camera| bearing(mark.direction, camera));
        commands.spawn((
            Name::new("Original hurt direction wedge"),
            Wedge(mark.sequence),
            Node {
                position_type: PositionType::Absolute,
                left: percent(50),
                top: percent(50),
                width: px(180),
                height: px(180),
                ..default()
            },
            UiTransform::from_translation(Val2::percent(-50, -50)),
            MaterialNode(materials.add(WedgeMaterial {
                state: Vec4::new(
                    angle.unwrap_or(0.),
                    mark.alpha(),
                    mark.critical as u8 as f32,
                    0.,
                ),
            })),
            // Original layer10, before ordinary HUD/crosshair (native20/21).
            GlobalZIndex(10),
            if visible && angle.is_some() {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            },
            Pickable::IGNORE,
        ));
    }
}

#[cfg(test)]
mod tests;
