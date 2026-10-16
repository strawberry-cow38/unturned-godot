//! Source binoculars, item type OPTIC (PlayerController.EquipHeldOptic, UpdateOptic,
//! CycleOpticZoom, ClearHeldOptic and the LMB dispatch; BinocularsView;
//! binoculars.gdshader; binoculars_overlay.gdshader).
//!
//! - **Carried** (lowered): the pair (`items/333.txt`, `items/333.png`) rides the hand
//!   hook with the source's NaturalHold carry (`Deploy_Equip`, as `Fuel_Equip` is not in the
//!   rig) at `HoldPos (0, 0.44, -0.14)` in view space and `HoldRoll (0, 0, -90)`. A second
//!   camera in front of the objectives renders the world at FOV / zoom, and one disc sunk
//!   into each eyepiece shows that single render at its screen position (the scope PiP).
//! - **Raised** (RMB held): the main camera's FOV is divided by the zoom, the arms are
//!   hidden, the retail overlay (`ui/binoculars_overlay.png`, the tank periscope overlay)
//!   covers the window, and the mouse turns 1/zoom as much. The PiP stops.
//! - **LMB** steps the zoom 4x -> 8x -> 12x -> 4x with the firemode click, raised or not.
//!
//! All of it is local presentation in the source; nothing reaches the server.
use super::*;
use crate::{hdr::viewport::ViewmodelViewport, use_items::Cell};
use bevy::math::{Affine3A, Mat3A, Vec3A};
use bevy::{
    asset::embedded_asset,
    audio::Volume,
    camera::{Hdr, RenderTarget, visibility::VisibilitySystems},
    core_pipeline::tonemapping::{DebandDither, Tonemapping},
    image::ImageSampler,
    light::NotShadowCaster,
    pbr::{ExtendedMaterial, MaterialExtension},
    render::render_resource::{AsBindGroup, TextureFormat},
    shader::ShaderRef,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow},
};

/// OpticZoomLevels.
pub(crate) const ZOOM_LEVELS: [f32; 3] = [4., 8., 12.];
/// OpticEyeX / OpticEyeY / OpticLensR: the 333 model's eyepiece rings (barrels along +Y).
const EYE: Vec2 = Vec2::new(0.0791, -0.259);
const LENS_RADIUS: f32 = 0.0372;
/// OpticLensInset: the discs sit up the barrel, inside the eyepieces.
const LENS_INSET: f32 = 0.030;
/// EquipHeldOptic's carry pose.
const HOLD_POSITION: Vec3 = Vec3::new(0., 0.44, -0.14);
const HOLD_ROLL_DEGREES: f32 = -90.;
/// BinocularsView.ObjectiveAhead.
const OBJECTIVE_AHEAD: f32 = 0.12;
/// binoculars.gdshader `vignette`.
const VIGNETTE: f32 = 0.30;
const SELECTOR_SOUND: &str = "sounds/firemode.wav";

#[derive(Resource, Default)]
pub(crate) struct Binoculars {
    held: Option<Cell>,
    index: usize,
    raised: bool,
    epoch: u64,
    /// The source harness's `UG_ADS=1` (`_opticForceRaise`): hold them up for a render.
    pub(crate) force_raise: bool,
}
impl Binoculars {
    pub(crate) fn held(&self) -> bool {
        self.held.is_some()
    }
    pub(crate) fn raised(&self) -> bool {
        self.raised
    }
    pub(crate) fn zoom(&self) -> f32 {
        ZOOM_LEVELS[self.index]
    }
    /// `_bino.Raised`: retail PlayerLook.enableZoom divides the main camera's FOV.
    pub(crate) fn raised_zoom(&self) -> Option<f32> {
        self.raised.then(|| self.zoom())
    }
    /// `if (_bino.Raised) sens /= Max(1, _bino.Zoom)` (retail shouldUseZoomFactorForSensitivity).
    pub(crate) fn look_sensitivity(&self) -> f32 {
        if self.raised { 1. / self.zoom().max(1.) } else { 1. }
    }
    /// One frame: a fresh equip starts at 4x, lowered (EquipHeldOptic); LMB steps the zoom
    /// (CycleOpticZoom); `want` is UpdateOptic's RMB test. True when the zoom stepped.
    pub(crate) fn step(&mut self, held: Option<Cell>, want: bool, cycle: bool) -> bool {
        if held != self.held {
            self.held = held;
            self.index = 0;
            self.raised = false;
        }
        if held.is_none() {
            self.raised = false;
            return false;
        }
        if cycle {
            self.index = (self.index + 1) % ZOOM_LEVELS.len();
        }
        self.raised = want || self.force_raise;
        cycle
    }
}

#[derive(Asset, AsBindGroup, Reflect, Debug, Clone)]
struct Lens {
    /// x vignette, yz the viewmodel target's oversize vs the screen.
    #[uniform(100)]
    params: Vec4,
    #[texture(101)]
    #[sampler(102)]
    scene: Handle<Image>,
}
impl MaterialExtension for Lens {
    fn fragment_shader() -> ShaderRef {
        "embedded://unturned_bevy/viewmodel/binoculars/lens.wesl".into()
    }
}
type LensMaterial = ExtendedMaterial<StandardMaterial, Lens>;

#[derive(Component)]
pub(crate) struct LensDisc(Vec3);
#[derive(Component)]
pub(crate) struct BinocularsCamera;
#[derive(Resource, Default)]
struct Pip {
    camera: Option<Entity>,
    image: Option<Handle<Image>>,
    material: Option<Handle<LensMaterial>>,
    lenses_on: Option<Entity>,
}

pub(super) struct BinocularsPlugin;
impl Plugin for BinocularsPlugin {
    fn build(&self, app: &mut App) {
        embedded_asset!(app, "binoculars/lens.wesl");
        let force = std::env::var("UG_ADS").is_ok_and(|v| v == "1");
        app.add_plugins(MaterialPlugin::<LensMaterial>::default())
            .insert_resource(Binoculars { force_raise: force, ..default() })
            .init_resource::<Pip>()
            // Local presentation only: a dedicated host has no hand, camera or window.
            .add_systems(
                Update,
                input
                    .after(crate::ui::UiUpdate)
                    .before(PlayerUpdate)
                    .run_if(crate::dedicated::has_local_player),
            )
            .add_systems(
                Update,
                hide_arms
                    .after(build_viewmodel)
                    .before(RigAnimation)
                    .run_if(crate::dedicated::has_local_player),
            )
            .add_systems(
                PostUpdate,
                pose.run_if(crate::dedicated::has_local_player)
                    .after(bevy::transform::TransformSystems::Propagate)
                    .before(bevy::light::SimulationLightSystems::UpdateDirectionalLightCascades)
                    .before(VisibilitySystems::UpdateFrusta),
            );
    }
}

/// The held OPTIC, if the snapshot's hand holds one (`KindOf` -> HandKind.Optic).
pub(crate) fn held_optic(snapshot: &serde_json::Value, catalog: &ItemCatalog) -> Option<Cell> {
    Cell::held(snapshot).filter(|cell| {
        snapshot["player"]["alive"] != false
            && catalog.items.get(&cell.id).is_some_and(|i| i.hand_tool["kind"] == "optic")
    })
}

#[allow(clippy::too_many_arguments)]
fn input(
    mut commands: Commands,
    mut binoculars: ResMut<Binoculars>,
    epoch: Res<crate::player::WorldSessionEpoch>,
    snapshot: Res<GameSnapshot>,
    catalog: Res<ItemCatalog>,
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    windows: Query<(&Window, &CursorOptions), With<PrimaryWindow>>,
    occupancy: Res<crate::vehicle::Occupancy>,
    (config, assets): (Res<WorldConfig>, Res<AssetServer>),
) {
    if binoculars.epoch != epoch.0 {
        let force_raise = binoculars.force_raise;
        *binoculars = Binoculars { epoch: epoch.0, force_raise, ..default() };
    }
    let held = held_optic(&snapshot.0, &catalog);
    // UpdateOptic: captured mouse, no UI, alive, not driving; LMB is on-foot only.
    let captured = windows
        .single()
        .is_ok_and(|(w, c)| w.focused && !c.visible && c.grab_mode != CursorGrabMode::None);
    let free = captured && !ui.captures_input() && occupancy.seat.is_none();
    let want = free && settings.pressed(GameAction::Aim, &keys, &mouse);
    let cycle = free && settings.just_pressed(GameAction::Fire, &keys, &mouse);
    if binoculars.step(held, want, cycle) && config.content_root.join(SELECTOR_SOUND).is_file() {
        // PlaySelectorSwitchSound: the firemode click, a detent between zoom stops.
        commands.spawn((
            AudioPlayer::new(assets.load(SELECTOR_SOUND)),
            PlaybackSettings::DESPAWN.with_volume(Volume::Linear(settings.master_volume)),
        ));
    }
}

/// `_viewmodel.SetShown(!want)`: raised, the pair is at the eyes and the arms are gone.
fn hide_arms(binoculars: Res<Binoculars>, mut cameras: Query<&mut Camera, With<ViewmodelCamera>>) {
    if binoculars.raised() {
        for mut camera in &mut cameras {
            camera.is_active = false;
        }
    }
}

/// The carried pose of the pair in world space: the hook's basis times HoldRoll, at the
/// hook plus HoldPos in view (frame) space (Viewmodel's DeployableMesh follow branch).
pub(crate) fn carried_pose(hook: &GlobalTransform, frame: &GlobalTransform) -> Affine3A {
    let hook = hook.affine();
    let roll = Mat3A::from_rotation_z(HOLD_ROLL_DEGREES.to_radians());
    let translation = hook.translation + Vec3A::from(frame.rotation() * HOLD_POSITION);
    Affine3A { matrix3: hook.matrix3 * roll, translation }
}
/// BinocularsView._Process: the PiP camera sits OBJECTIVE_AHEAD up the barrels (+Y),
/// looking along them (Rx(+90) takes a camera's -Z onto +Y).
pub(crate) fn objective_pose(carried: Affine3A) -> Transform {
    let (_, rotation, translation) = carried.to_scale_rotation_translation();
    let along = Vec3::from(carried.matrix3.y_axis).normalize_or_zero();
    Transform::from_translation(translation + along * OBJECTIVE_AHEAD)
        .with_rotation(rotation * Quat::from_rotation_x(std::f32::consts::FRAC_PI_2))
}

#[allow(clippy::too_many_arguments, clippy::type_complexity)]
fn pose(
    mut commands: Commands,
    binoculars: Res<Binoculars>,
    state: Res<ViewmodelState>,
    mut pip: ResMut<Pip>,
    settings: Option<Res<Settings>>,
    viewport: Option<Res<ViewmodelViewport>>,
    windows: Query<&Window, With<PrimaryWindow>>,
    (mut images, mut materials, mut meshes): (
        ResMut<Assets<Image>>,
        ResMut<Assets<LensMaterial>>,
        ResMut<Assets<Mesh>>,
    ),
    parents: Query<&ChildOf>,
    vm_cameras: Query<&Camera, (With<ViewmodelCamera>, Without<BinocularsCamera>)>,
    main: Query<(&Projection, Option<&DistanceFog>), (With<PlayerCamera>, Without<BinocularsCamera>)>,
    mut transforms: ParamSet<(
        Query<&GlobalTransform>,
        Query<(&mut Transform, &mut GlobalTransform), With<HeldMesh>>,
        Query<(&LensDisc, &mut GlobalTransform), Without<HeldMesh>>,
        Query<(&mut Transform, &mut GlobalTransform, &mut Camera, &mut Projection), With<BinocularsCamera>>,
    )>,
) {
    let held = binoculars.held().then_some(state.gun).flatten();
    let Some(gun) = held else {
        if let Some(camera) = pip.camera.take() {
            commands.entity(camera).try_despawn();
        }
        pip.lenses_on = None;
        return;
    };
    let (Some(frame), Ok(parent)) = (state.frame, parents.get(gun).map(|p| p.parent())) else {
        return;
    };
    let reads = transforms.p0();
    let (Ok(hook), Ok(frame)) = (reads.get(parent).copied(), reads.get(frame).copied()) else {
        return;
    };
    let carried = carried_pose(&hook, &frame);
    if let Ok((mut local, mut global)) = transforms.p1().get_mut(gun) {
        *local = Transform::from_matrix((hook.affine().inverse() * carried).into());
        *global = GlobalTransform::from(carried);
    }
    for (disc, mut global) in &mut transforms.p2() {
        let local = Transform::from_translation(disc.0).with_rotation(Quat::from_rotation_x(std::f32::consts::FRAC_PI_2));
        *global = GlobalTransform::from(carried * local.compute_affine());
    }
    // The single magnified render the discs cut into (BinocularsView._world).
    let window = windows.single().map_or(Vec2::new(1280., 720.), |w| w.size().max(Vec2::ONE));
    if pip.image.is_none() {
        let scope = settings.as_ref().map_or(720, |s| s.graphics.scope_quality.scope_size());
        let height = (scope * 2).clamp(540, (window.y as u32).max(540));
        let width = ((height as f32 * window.x / window.y).round() as u32).max(16);
        let mut target = Image::new_target_texture(width, height, TextureFormat::Rgba16Float, None);
        target.sampler = ImageSampler::linear();
        pip.image = Some(images.add(target));
    }
    let image = pip.image.clone().unwrap();
    let over = viewport.as_ref().map_or(Vec2::ONE, |v| v.target.as_vec2() / v.screen.as_vec2());
    let material = pip
        .material
        .get_or_insert_with(|| {
            materials.add(ExtendedMaterial {
                base: StandardMaterial { unlit: true, cull_mode: None, fog_enabled: false, ..default() },
                extension: Lens { params: Vec4::new(VIGNETTE, over.x, over.y, 0.), scene: image.clone() },
            })
        })
        .clone();
    let params = Vec4::new(VIGNETTE, over.x, over.y, 0.);
    if materials.get(&material).is_some_and(|m| m.extension.params != params)
        && let Some(mut lens) = materials.get_mut(&material)
    {
        lens.extension.params = params;
    }
    if pip.lenses_on != Some(gun) {
        // Viewmodel.AddHeldLens: a quad of radius * 1.05 in each eyepiece, facing the eye.
        let disc = meshes.add(Rectangle::from_length(LENS_RADIUS * 1.05 * 2.));
        for side in [-EYE.x, EYE.x] {
            let at = Vec3::new(side, EYE.y + LENS_INSET, 0.);
            commands.spawn((
                Name::new("Binocular eyepiece lens"),
                LensDisc(at),
                Mesh3d(disc.clone()),
                MeshMaterial3d(material.clone()),
                Transform::from_translation(at).with_rotation(Quat::from_rotation_x(std::f32::consts::FRAC_PI_2)),
                RenderLayers::layer(LAYER),
                NotShadowCaster,
                ChildOf(gun),
            ));
        }
        pip.lenses_on = Some(gun);
    }
    let Ok((projection, fog)) = main.single() else { return };
    let Projection::Perspective(main_projection) = projection else { return };
    let active = !binoculars.raised() && vm_cameras.iter().any(|c| c.is_active);
    let objective = objective_pose(carried);
    let fov = main_projection.fov / binoculars.zoom().max(1.);
    match pip.camera {
        Some(camera) => {
            if let Ok((mut local, mut global, mut cam, mut projection)) = transforms.p3().get_mut(camera) {
                *local = objective;
                *global = GlobalTransform::from(objective);
                cam.is_active = active;
                if let Projection::Perspective(p) = &mut *projection {
                    p.fov = fov;
                    p.near = main_projection.near;
                    p.far = main_projection.far;
                }
            }
            match fog {
                Some(fog) => commands.entity(camera).insert(fog.clone()),
                None => commands.entity(camera).remove::<DistanceFog>(),
            };
        }
        None => {
            let size = images.get(&image).map_or(UVec2::new(16, 9), |i| i.size());
            pip.camera = Some(
                commands
                    .spawn((
                        Name::new("Binoculars objective camera"),
                        BinocularsCamera,
                        Camera3d::default(),
                        Hdr,
                        Camera { order: -1, is_active: false, ..default() },
                        RenderTarget::Image(image.into()),
                        Projection::Perspective(PerspectiveProjection {
                            fov,
                            aspect_ratio: size.x as f32 / size.y as f32,
                            near: main_projection.near,
                            far: main_projection.far,
                            ..default()
                        }),
                        Tonemapping::None,
                        DebandDither::Disabled,
                        objective,
                        RenderLayers::layer(0),
                    ))
                    .id(),
            );
        }
    }
}

#[cfg(test)]
mod tests;
