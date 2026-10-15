//! TankOptics.cs and PlayerController.UpdateTankOptics. These optics change only
//! presentation; accepted seating/hatch state and mounted aim/fire remain separate.
use super::{UiState, UiUpdate};
use crate::{
    avatar::Perspective,
    player::{GameSnapshot, PlayerCamera, PlayerCameraUpdate, PlayerUpdate, WorldSessionEpoch},
    settings::{GameAction, Settings},
    vehicle::{Occupancy, VehicleBody, data, turrets::Mounts},
    world::WorldConfig,
};
use bevy::{
    asset::embedded_asset,
    ecs::system::SystemParam,
    image::{ImageLoaderSettings, ImageSampler},
    prelude::*,
    render::render_resource::AsBindGroup,
    shader::ShaderRef,
    window::PrimaryWindow,
};

#[derive(Clone, Copy, Default, Debug, PartialEq, Eq)]
pub(crate) enum Mode {
    #[default]
    None,
    Periscope,
    Gunsight,
}
#[derive(Resource, Default)]
pub(crate) struct TankOptics {
    pub mode: Mode,
    source_mode: Mode,
    index: usize,
    epoch: u64,
}
impl TankOptics {
    pub fn zoom(&self) -> f32 {
        if self.source_mode == Mode::Gunsight {
            [8., 12., 4.][self.index]
        } else {
            1.
        }
    }
    /// Original GunsightActive excludes HideViewmodelDebug: diagnostics hide
    /// the optic image but must not gain a second generic crosshair.
    pub fn gunsight_active(&self) -> bool {
        self.source_mode == Mode::Gunsight
    }
    pub fn look_sensitivity(&self) -> f32 {
        1. / self.zoom()
    }
}
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct TankOpticsInput;
#[derive(SystemSet, Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub(crate) struct OpticsUpdate;
#[derive(Resource, Default)]
struct CameraState {
    camera: Option<Entity>,
    base_fov: f32,
}
#[derive(Component)]
struct Overlay;
#[derive(Asset, TypePath, AsBindGroup, Debug, Clone)]
struct OpticsMaterial {
    // screen aspect, source mode (1 periscope, 2 gunsight), spare
    #[uniform(0)]
    settings: Vec4,
    #[texture(1)]
    #[sampler(2)]
    mask: Handle<Image>,
    #[texture(3)]
    #[sampler(4)]
    reticle: Handle<Image>,
}
impl UiMaterial for OpticsMaterial {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/ui/tank_optics/overlay.wesl".into()
    }
}
pub(super) struct TankOpticsPlugin;
impl Plugin for TankOpticsPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "tank_optics/overlay.wesl");
        app.init_resource::<TankOptics>()
            .init_resource::<CameraState>()
            .add_plugins(UiMaterialPlugin::<OpticsMaterial>::default())
            .add_systems(Startup, spawn);
        install_update_systems(app);
    }
}
fn install_update_systems(app: &mut App) {
    install_input_system(app);
    app.add_systems(Update, apply.in_set(OpticsUpdate).after(PlayerCameraUpdate));
}
pub(crate) fn install_input_system(app: &mut App) {
    app.add_systems(
        Update,
        input
            .in_set(TankOpticsInput)
            .after(UiUpdate)
            .after(crate::avatar::PerspectiveInput)
            .before(PlayerUpdate),
    );
}
fn spawn(
    mut commands: Commands,
    assets: Res<AssetServer>,
    mut materials: ResMut<Assets<OpticsMaterial>>,
) {
    let linear = |settings: &mut ImageLoaderSettings| {
        settings.sampler = ImageSampler::linear();
    };
    let mask = assets
        .load_builder()
        .with_settings(linear)
        .load("ui/binoculars_overlay.png");
    let reticle = assets
        .load_builder()
        .with_settings(linear)
        .load("scope_8x_reticle.png");
    commands.spawn((
        Name::new("Original tank periscope and gunsight"),
        Overlay,
        Node {
            position_type: PositionType::Absolute,
            width: percent(100),
            height: percent(100),
            ..default()
        },
        MaterialNode(materials.add(OpticsMaterial {
            settings: Vec4::ZERO,
            mask,
            reticle,
        })),
        Visibility::Hidden,
        // Source CanvasLayer6: above the layer5 first-person composite, below
        // HUD10 and all dashboards. Native default UI camera follows the VM pass.
        GlobalZIndex(-10),
        Pickable::IGNORE,
    ));
}
fn selected_mode(
    alive: bool,
    first_person: bool,
    hidden: bool,
    tank: bool,
    seat: u8,
    head_out: bool,
) -> Mode {
    if !alive || !first_person || hidden || !tank {
        return Mode::None;
    }
    match seat {
        0 => Mode::Periscope,
        1 if !head_out => Mode::Gunsight,
        _ => Mode::None,
    }
}
#[derive(SystemParam)]
struct Facts<'w, 's> {
    snapshot: Res<'w, GameSnapshot>,
    view: Res<'w, Perspective>,
    occupancy: Res<'w, Occupancy>,
    vehicles: Query<'w, 's, (&'static VehicleBody, Option<&'static Mounts>)>,
    config: Res<'w, WorldConfig>,
}
impl Facts<'_, '_> {
    fn seat(&self) -> Option<(u32, u8)> {
        let state = &self.snapshot.0;
        if state["player"].get("vehicleNetId").is_some() {
            // BridgeHost derives these fields from the accepted vehicle replica
            // for both loopback and UDP. Zero is an explicit exit, never a reason
            // to reuse the previous frame's native seat.
            let net_id = u32::try_from(state["player"]["vehicleNetId"].as_u64()?).ok()?;
            let seat = u8::try_from(state["player"]["vehicleSeat"].as_u64()?).ok()?;
            return (net_id != 0).then_some((net_id, seat));
        }
        if let Some(vehicles) = state["vehicles"].as_array() {
            let owner = state["playerId"].as_u64().filter(|id| *id != 0)?;
            return vehicles.iter().find_map(|row| {
                let seat = if row["driverPlayerId"].as_u64() == Some(owner) {
                    0
                } else {
                    let index = row["passengers"]
                        .as_array()?
                        .iter()
                        .position(|id| id.as_u64() == Some(owner))?;
                    u8::try_from(index + 1).ok()?
                };
                Some((u32::try_from(row["netId"].as_u64()?).ok()?, seat))
            });
        }
        // Direct render fixtures have no authority replica. Regular gameplay
        // always takes one of the accepted paths above, including empty fleets.
        self.occupancy.seat.map(|seat| (seat.net_id, seat.seat))
    }
    fn mode(&self) -> Mode {
        self.seat()
            .and_then(|(net_id, seat)| {
                self.vehicles
                    .iter()
                    .find(|(body, _)| body.net_id == net_id)
                    .map(|(body, mounts)| {
                        // Mount adoption follows PlayerUpdate. Read the same accepted fact
                        // now so sensitivity/zoom sees a hatch change on its receiving frame.
                        let head_out = self.snapshot.0["vehicleMounts"]
                            .as_array()
                            .into_iter()
                            .flatten()
                            .find(|row| row["vehicleNetId"].as_u64() == Some(body.net_id as u64))
                            .and_then(|row| row["headOut"].as_bool())
                            .unwrap_or_else(|| mounts.is_some_and(|m| m.head_out));
                        selected_mode(
                            self.snapshot.0["connected"] == true
                                && self.snapshot.0["player"]["alive"] != false,
                            self.view.first_person,
                            false,
                            data::definition(body.type_id)
                                .is_some_and(|d| d.spec.turret_meshes.is_some()),
                            seat,
                            head_out,
                        )
                    })
            })
            .unwrap_or(Mode::None)
    }
}
fn input(
    mut optics: ResMut<TankOptics>,
    epoch: Res<WorldSessionEpoch>,
    facts: Facts,
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
) {
    if optics.epoch != epoch.0 {
        *optics = TankOptics {
            epoch: epoch.0,
            ..default()
        };
    }
    optics.source_mode = facts.mode();
    optics.mode = if facts.config.fixed_camera.is_some() {
        Mode::None
    } else {
        optics.source_mode
    };
    if optics.gunsight_active()
        && !ui.captures_input()
        && settings.just_pressed(GameAction::Aim, &keys, &mouse)
    {
        // Index deliberately survives death, head-out and seat exits, as on the
        // original PlayerController. A new accepted session creates a new player.
        optics.index = (optics.index + 1) % 3;
    }
}
#[cfg(test)]
pub(crate) fn run_input_system(world: &mut World) {
    use bevy::ecs::system::RunSystemOnce;
    world.run_system_once(input).unwrap();
}
fn apply(
    mut optics: ResMut<TankOptics>,
    facts: Facts,
    mut saved: ResMut<CameraState>,
    mut cameras: Query<(Entity, &mut Projection), With<PlayerCamera>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    mut overlay: Query<(&mut Visibility, &MaterialNode<OpticsMaterial>), With<Overlay>>,
    mut materials: ResMut<Assets<OpticsMaterial>>,
) {
    // Seat materialization can complete later in VehicleUpdate. Reconcile its
    // visual gate after that stage without consuming the input edge twice.
    optics.source_mode = facts.mode();
    optics.mode = if facts.config.fixed_camera.is_some() {
        Mode::None
    } else {
        optics.source_mode
    };
    let active = optics.mode != Mode::None;
    let mut shown = false;
    if let Ok((entity, mut projection)) = cameras.single_mut() {
        if let Projection::Perspective(p) = &mut *projection {
            if active {
                if saved.camera != Some(entity) {
                    saved.base_fov = p.fov;
                }
                saved.camera = Some(entity);
                p.fov = saved.base_fov / optics.zoom();
                shown = true;
            } else if saved.camera.take().is_some() {
                p.fov = saved.base_fov;
            }
        }
    } else {
        saved.camera = None;
    }
    let aspect = windows
        .single()
        .map_or(16. / 9., |w| w.width() / w.height().max(1.));
    for (mut visibility, handle) in &mut overlay {
        *visibility = if shown {
            Visibility::Inherited
        } else {
            Visibility::Hidden
        };
        if let Some(mut material) = materials.get_mut(&handle.0) {
            material.settings = Vec4::new(
                aspect,
                match optics.mode {
                    Mode::None => 0.,
                    Mode::Periscope => 1.,
                    Mode::Gunsight => 2.,
                },
                0.,
                0.,
            );
        }
    }
}
#[cfg(test)]
mod tests;
