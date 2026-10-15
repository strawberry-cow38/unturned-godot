//! Source local/remote footsteps, landing impacts and local swim strokes.
//! These cosmetic voices never publish damage or zombie-hearing commands.
mod rules;
mod voices;
use crate::{
    avatar::remotes::{RemotePlayer, RemotePlayersUpdate},
    impact::surfaces::{Surface, Surfaces},
    player::{
        PlayerBody, PlayerInput, PublishPlayer, SimulatePlayer, WorldSessionEpoch,
        simulation_paused,
    },
    sound::shelter::Shelter,
    ui::{UiModel, UiState},
    vehicle::Occupancy,
    world::{
        WorldConfig, WorldState,
        origin::WorldOrigin,
        rain::{RainState, RainUpdate},
    },
};
use bevy::{ecs::system::SystemParam, prelude::*};
use bevy_rapier3d::prelude::*;
use rules::*;
use std::collections::HashMap;

pub(crate) struct FootstepPlugin;
impl Plugin for FootstepPlugin {
    fn build(&self, app: &mut App) {
        app.init_resource::<Banks>()
            .init_resource::<LocalSteps>()
            .init_resource::<RemoteSteps>()
            .init_resource::<voices::Random>()
            .add_message::<Footfall>()
            .add_systems(Startup, setup)
            .add_systems(FixedUpdate, local_steps.before(SimulatePlayer))
            .add_systems(
                FixedUpdate,
                local_swim
                    .after(SimulatePlayer)
                    .before(PhysicsSet::SyncBackend),
            )
            .add_systems(
                FixedUpdate,
                local_landing
                    .after(PhysicsSet::Writeback)
                    .before(PublishPlayer),
            )
            .add_systems(
                Update,
                (remote_steps, voices::play, voices::update)
                    .chain()
                    .after(RemotePlayersUpdate)
                    .after(RainUpdate),
            );
    }
}
fn setup(mut banks: ResMut<Banks>, config: Res<WorldConfig>) {
    *banks = Banks::load(&config.content_root);
}

#[derive(Clone, Copy, Debug)]
enum Sound {
    Step(Surface, bool),
    Landing(Surface),
    Swim(&'static str),
}
#[derive(Message, Clone, Copy, Debug)]
struct Footfall {
    sound: Sound,
    at: Vec3,
    db: f32,
    unit: f32,
    range: f32,
    pitch_jitter: f32,
    epoch: u64,
}
impl Footfall {
    fn step(at: Vec3, surface: Surface, stance: &str, speed: f32, epoch: u64) -> Self {
        Self {
            sound: Sound::Step(surface, stance == "sprint" || speed > 4.5),
            at,
            db: step_db(stance),
            unit: 4.,
            range: 30.,
            pitch_jitter: 0.06,
            epoch,
        }
    }
    fn landing(at: Vec3, surface: Surface, vertical_speed: f32, epoch: u64) -> Self {
        Self {
            sound: Sound::Landing(surface),
            at,
            db: landing_db(vertical_speed),
            unit: 5.,
            range: 40.,
            pitch_jitter: 0.,
            epoch,
        }
    }
}
#[derive(Resource, Default)]
struct LocalSteps {
    epoch: u64,
    acc: f32,
}
#[derive(Default)]
struct RemoteStride {
    acc: f32,
    grounded: bool,
}
#[derive(Resource, Default)]
struct RemoteSteps {
    epoch: u64,
    players: HashMap<Entity, RemoteStride>,
}

#[derive(SystemParam)]
struct Ground<'w, 's> {
    context: ReadRapierContext<'w, 's>,
    surfaces: Surfaces<'w, 's>,
    shelter: Shelter<'w, 's>,
    world: Res<'w, WorldState>,
    origin: Res<'w, WorldOrigin>,
    rain: Res<'w, RainState>,
}
impl Ground<'_, '_> {
    fn surface(&self, at: Vec3, owner: Entity) -> Option<Surface> {
        // Source WaveField.Flat=true: WaterSurfaceY is the mean sea level.
        let surface = if self.world.sea_level.is_some_and(|sea| at.y < sea + 0.1) {
            Surface::Water
        } else {
            let context = self.context.single().ok()?;
            let (entity, _) = context.cast_ray(
                at + Vec3::Y * 0.3,
                Vec3::NEG_Y,
                0.9,
                true,
                QueryFilter::default()
                    .exclude_sensors()
                    .exclude_collider(owner)
                    .groups(CollisionGroups::new(Group::ALL, Group::GROUP_1)),
            )?;
            self.surfaces.at(entity, at).0
        };
        Some(
            if self.rain.puddle >= 0.35
            && matches!(surface, Surface::Concrete | Surface::Metal)
            // Exactly the same local+origin frame sent to surface.wesl.
            && puddle_wet(at.xz() + self.origin.shader_offset(), self.rain.puddle)
            && !self.shelter.covered(at + Vec3::Y * 0.2)
            {
                Surface::Water
            } else {
                surface
            },
        )
    }
}

#[derive(SystemParam)]
struct LocalContext<'w> {
    model: Res<'w, UiModel>,
    ui: Res<'w, UiState>,
    epoch: Res<'w, WorldSessionEpoch>,
    occupancy: Res<'w, Occupancy>,
    prop: Option<Res<'w, crate::world::interactables::PropOccupancy>>,
}
impl LocalContext<'_> {
    fn active(&self) -> bool {
        self.model.connected
            && self.model.alive
            && !simulation_paused(&self.ui)
            && self.occupancy.seat.is_none()
            && self.prop.as_ref().is_none_or(|p| p.seat.is_none())
    }
}
fn local_steps(
    mut steps: ResMut<LocalSteps>,
    state: LocalContext,
    input: Res<PlayerInput>,
    time: Res<Time<Fixed>>,
    ground: Ground,
    bodies: Query<(Entity, &Transform, &PlayerBody)>,
    mut sounds: MessageWriter<Footfall>,
) {
    if steps.epoch != state.epoch.0 || !state.model.connected {
        *steps = LocalSteps {
            epoch: state.epoch.0,
            acc: 0.,
        };
    }
    if !state.active() {
        return;
    }
    let Ok((entity, pose, body)) = bodies.single() else {
        return;
    };
    let speed = body.velocity().xz().length();
    if body.grounded() && !matches!(body.stance(), "swim" | "climb" | "mantle") {
        if input.moving() && speed > 0.3 {
            if advance(
                &mut steps.acc,
                speed * time.delta_secs(),
                stride(body.stance()),
            ) {
                let surface = ground.surface(pose.translation, entity).unwrap_or_default();
                sounds.write(Footfall::step(
                    pose.translation,
                    surface,
                    body.stance(),
                    speed,
                    state.epoch.0,
                ));
            }
        } else {
            steps.acc = steps.acc.min(0.9);
        }
    }
}
fn local_swim(
    mut steps: ResMut<LocalSteps>,
    state: LocalContext,
    time: Res<Time<Fixed>>,
    bodies: Query<(&Transform, &PlayerBody)>,
    mut sounds: MessageWriter<Footfall>,
) {
    if !state.active() {
        return;
    }
    let Ok((pose, body)) = bodies.single() else {
        return;
    };
    let speed = body.velocity().xz().length();
    if body.stance() == "swim"
        && speed > 0.4
        && advance(&mut steps.acc, speed * time.delta_secs(), 1.4)
    {
        sounds.write(Footfall {
            sound: Sound::Swim(swim_bank(speed)),
            at: pose.translation,
            db: -4.,
            unit: 5.,
            range: 40.,
            pitch_jitter: 0.05,
            epoch: state.epoch.0,
        });
    }
}
fn local_landing(
    state: LocalContext,
    ground: Ground,
    bodies: Query<(
        Entity,
        &Transform,
        &PlayerBody,
        &KinematicCharacterControllerOutput,
    )>,
    mut sounds: MessageWriter<Footfall>,
) {
    if !state.active() {
        return;
    }
    let Ok((entity, pose, body, output)) = bodies.single() else {
        return;
    };
    // Run before PublishPlayer replaces precontact velocity with resolved travel.
    if body.stance() != "swim" && !body.grounded() && output.grounded && body.velocity().y < -2.5 {
        sounds.write(Footfall::landing(
            pose.translation,
            ground.surface(pose.translation, entity).unwrap_or_default(),
            body.velocity().y,
            state.epoch.0,
        ));
    }
}
fn remote_steps(
    mut steps: ResMut<RemoteSteps>,
    epoch: Res<WorldSessionEpoch>,
    ui: Res<UiState>,
    model: Res<UiModel>,
    time: Res<Time>,
    ground: Ground,
    players: Query<(Entity, &Transform, &RemotePlayer)>,
    mut sounds: MessageWriter<Footfall>,
) {
    if steps.epoch != epoch.0 || !model.connected {
        steps.players.clear();
        steps.epoch = epoch.0;
    }
    steps.players.retain(|entity, _| players.contains(*entity));
    if !model.connected || simulation_paused(&ui) {
        return;
    }
    let dt = time.delta_secs();
    for (entity, pose, player) in &players {
        let previous = steps.players.entry(entity).or_insert(RemoteStride {
            acc: 0.,
            grounded: true,
        });
        let motion = player.foot_motion;
        if motion.seated || !player.alive || motion.snap || dt <= 0. {
            previous.acc = 0.;
            previous.grounded = true;
            continue;
        }
        if motion.speed <= 0.3 && motion.vertical_speed.abs() <= 0.5 && previous.grounded {
            continue;
        }
        let surface = ground.surface(pose.translation, entity);
        let grounded = surface.is_some();
        if grounded && motion.speed > 0.3 {
            if advance(&mut previous.acc, motion.speed * dt, stride(motion.stance)) {
                sounds.write(Footfall::step(
                    pose.translation,
                    surface.unwrap(),
                    motion.stance,
                    motion.speed,
                    epoch.0,
                ));
            }
        } else if !grounded {
            previous.acc = 0.;
        } else {
            previous.acc = previous.acc.min(0.9);
        }
        if grounded && !previous.grounded && motion.vertical_speed < -2.5 {
            sounds.write(Footfall::landing(
                pose.translation,
                surface.unwrap(),
                motion.vertical_speed,
                epoch.0,
            ));
        }
        previous.grounded = grounded;
    }
}

#[cfg(test)]
mod tests;
