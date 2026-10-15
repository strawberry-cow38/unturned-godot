//! Player-local light switches from PlayerController; equipped facts stay authoritative.
mod diagnostics;
pub(crate) use diagnostics::LightDiagnostics;
use super::*;
use crate::player::WorldSessionEpoch;
use bevy::{
    asset::RenderAssetUsages,
    image::ImageSampler,
    light::NotShadowCaster,
    render::render_resource::{Extent3d, TextureDimension, TextureFormat},
};
use bevy_rapier3d::prelude::*;
use serde_json::Value;
use std::collections::HashMap;

#[derive(Resource, Default)]
pub(crate) struct Switches {
    epoch: u64,
    weapon: Option<(u16, u64, [i64; 5])>,
    vision_voice: Option<Entity>,
    pub(super) clicks: u64,
    tactical: bool,
    held: bool,
    head: bool,
    vision: bool,
    pub(crate) military: bool,
    pub(crate) nightvision: bool,
    pub(crate) held_lit: bool,
    pub(crate) head_lit: bool,
    pub(crate) tactical_id: u16,
    pub(crate) tactical_lit: bool,
}
impl Switches {
    pub(crate) fn surface_lit(&self, id: u16) -> bool {
        match id {
            151 | 152 => self.tactical_lit && self.tactical_id == id,
            1199 => self.head_lit,
            334 | 1044 => self.nightvision && self.military == (id == 334),
            _ => self.held_lit,
        }
    }
}
#[derive(Component)]
pub(crate) struct LightSurface(pub u16);
#[derive(Component)]
pub(super) struct RailEmitter;
#[derive(Resource, Default)]
struct Visuals {
    beam: Option<Entity>,
    dot: Option<Entity>,
    lamps: [Option<Entity>; 3],
    masks: HashMap<AssetId<Image>, Handle<Image>>,
}
#[derive(Component)]
struct LightVisual;
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct LightInput;
#[derive(SystemSet, Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) struct LightPresentation;
pub(super) struct LightPlugin;
impl Plugin for LightPlugin {
    fn build(&self, app: &mut App) {
        diagnostics::install(app);
        app.init_resource::<Switches>()
            .init_resource::<Visuals>()
            .add_systems(Update, input.after(animate_viewmodel).in_set(LightInput))
            .add_systems(
                PostUpdate,
                render
                    .in_set(LightPresentation)
                    .after(bevy::transform::TransformSystems::Propagate)
                    .after(crate::avatar::AvatarMuzzleUpdate)
                    .before(bevy::camera::visibility::VisibilitySystems::CalculateBounds)
                    .before(bevy::camera::visibility::VisibilitySystems::VisibilityPropagate)
                    .before(bevy::light::SimulationLightSystems::UpdateBounds),
            )
            .add_systems(Update, emission.after(LightInput));
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Toggle {
    Rail,
    Held,
    Head,
    Vision,
}
fn toggle_target(rail: u16, glasses: u16) -> Option<Toggle> {
    match rail {
        151 | 152 => Some(Toggle::Rail),
        _ => match glasses {
            334 | 1044 => Some(Toggle::Vision),
            1199 => Some(Toggle::Head),
            _ => None,
        },
    }
}
fn ready_to_switch(target: Toggle, ready: bool, reload: bool, hammer: bool) -> bool {
    match target {
        Toggle::Rail => ready && !reload && !hammer,
        Toggle::Held => ready,
        _ => true,
    }
}
fn input(
    mut switches: ResMut<Switches>,
    snapshot: Res<GameSnapshot>,
    epoch: Res<WorldSessionEpoch>,
    status: Res<ViewmodelStatus>,
    catalog: Res<ItemCatalog>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    ui: Res<UiState>,
    mut commands: Commands,
    assets: Res<AssetServer>,
    config: Res<WorldConfig>,
    occupancy: Option<Res<crate::vehicle::Occupancy>>,
) {
    let connected = snapshot.0["connected"].as_bool().unwrap_or(false);
    let alive = snapshot.0["player"]["alive"].as_bool().unwrap_or(true);
    if switches.epoch != epoch.0 || !connected {
        if let Some(previous) = switches.vision_voice.take() {
            commands.entity(previous).try_despawn();
        }
        let clicks = switches.clicks;
        *switches = Switches {
            epoch: epoch.0,
            clicks,
            ..default()
        };
        return;
    }
    let held = snapshot.0["player"]["heldItemId"].as_u64().unwrap_or(0) as u16;
    let weapon = (
        held,
        snapshot.0["weapon"]["selectionVersion"]
            .as_u64()
            .unwrap_or(0),
        ["sightId", "tacticalId", "gripId", "barrelId", "magazineId"].map(|key| {
            snapshot.0["weapon"]["attachments"][key]
                .as_i64()
                .unwrap_or(-1)
        }),
    );
    // ApplyInstalledAttachments resets UseableGun's switch on every equip or
    // changed fit, including drawing the same physical weapon again.
    if switches.weapon.as_ref() != Some(&weapon) {
        switches.tactical = false;
        switches.weapon = Some(weapon);
    }
    let item = catalog.items.get(&held);
    let holding_light = item.is_some_and(|i| i.melee["light"].as_bool() == Some(true));
    if !holding_light {
        switches.held = false;
    }
    let rail = snapshot.0["weapon"]["attachments"]["tacticalId"]
        .as_u64()
        .unwrap_or(0) as u16;
    let glasses = snapshot.0["inventory"]["worn"]["glasses"]
        .as_u64()
        .unwrap_or(0) as u16;
    if alive && ui.screen == Screen::Playing {
        let target = if holding_light && settings.just_pressed(GameAction::Aim, &keys, &mouse) {
            Some(Toggle::Held)
        } else if settings.just_pressed(GameAction::Flashlight, &keys, &mouse)
            && !(occupancy
                .as_ref()
                .and_then(|o| o.seat)
                .is_some_and(|s| s.driver)
                && settings.bindings.get(&GameAction::Flashlight)
                    == settings.bindings.get(&GameAction::VehicleIgnition))
        {
            toggle_target(rail, glasses)
        } else {
            None
        };
        if let Some(target) = target.filter(|target| {
            ready_to_switch(
                *target,
                status.ready && status.held == held,
                snapshot.0["weapon"]["reloading"].as_bool().unwrap_or(false),
                snapshot.0["weapon"]["rechambering"]
                    .as_bool()
                    .unwrap_or(false),
            )
        }) {
            match target {
                Toggle::Rail => switches.tactical = !switches.tactical,
                Toggle::Held => switches.held = !switches.held,
                Toggle::Head => switches.head = !switches.head,
                Toggle::Vision => switches.vision = !switches.vision,
            }
            if target == Toggle::Vision {
                let path = format!(
                    "audio/misc/general_target_{}_beep.wav",
                    if switches.vision { "on" } else { "off" }
                );
                if config.content_root.join(&path).is_file() {
                    if let Some(previous) = switches.vision_voice.take() {
                        commands.entity(previous).try_despawn();
                    }
                    switches.vision_voice = Some(weapon_fx::spawn_voice(
                        &mut commands,
                        &assets,
                        &settings,
                        None,
                        path,
                        1.,
                        1.,
                        false,
                    ));
                } else {
                    switches.clicks += 1;
                }
            } else {
                switches.clicks += 1;
            }
        }
    }
    switches.held_lit = alive && switches.held && holding_light;
    switches.head_lit = alive && switches.head && glasses == 1199;
    switches.nightvision = alive && switches.vision && matches!(glasses, 334 | 1044);
    switches.military = glasses == 334;
    switches.tactical_id = rail;
    switches.tactical_lit = alive && switches.tactical && matches!(rail, 151 | 152);
}
#[derive(Clone, Copy, Debug)]
struct Spot {
    range: f32,
    cone: f32,
    energy: f32,
    color: Color,
    enabled: bool,
}
fn spot(value: &Value) -> Spot {
    let number = |key: &str, base: f32| value[key].as_f64().unwrap_or(base as f64) as f32;
    let c = &value["spotColor"];
    Spot {
        range: number("spotRange", 64.),
        cone: number("spotAngleFull", 90.).to_radians() * 0.5,
        energy: number("spotIntensity", 1.3),
        color: Color::srgb(
            c["r"].as_f64().unwrap_or(245. / 255.) as f32,
            c["g"].as_f64().unwrap_or(223. / 255.) as f32,
            c["b"].as_f64().unwrap_or(147. / 255.) as f32,
        ),
        enabled: value["spotEnabled"].as_bool().unwrap_or(true),
    }
}
fn torch(catalog: &ItemCatalog) -> Spot {
    spot(
        catalog
            .items
            .values()
            .find(|i| i.asset.as_deref() == Some("flashlight"))
            .map_or(&Value::Null, |i| &i.melee),
    )
}
fn headlamp() -> Spot {
    Spot {
        range: 90.,
        cone: 37_f32.to_radians(),
        energy: 2.1,
        color: Color::WHITE,
        enabled: true,
    }
}
fn beam_pose(emitter: Vec3, point: Vec3) -> Option<Transform> {
    let span = point - emitter;
    let len = span.length();
    if len < 0.05 || !len.is_finite() {
        return None;
    }
    let y = span / len;
    let mut x = y.cross(Vec3::Y);
    if x.length_squared() < 1e-6 {
        x = y.cross(Vec3::X);
    }
    let x = x.normalize();
    let z = x.cross(y).normalize();
    Some(Transform {
        translation: emitter + y * (len * 0.5),
        rotation: Quat::from_mat3(&Mat3::from_cols(x, y, z)),
        scale: Vec3::new(1., len, 1.),
    })
}
fn dot_size(distance: f32, fov: f32) -> f32 {
    0.02 * (distance.max(0.05) * (fov.clamp(1_f32.to_radians(), 179_f32.to_radians()) * 0.5).tan())
        .powf(0.85)
}
fn soft_disc() -> Image {
    let mut data = Vec::with_capacity(64 * 64 * 4);
    for y in 0..64 {
        for x in 0..64 {
            let r = (Vec2::new((x as f32 + 0.5) / 64., (y as f32 + 0.5) / 64.) - Vec2::splat(0.5))
                .length()
                * 2.;
            let a = (1. - r).clamp(0., 1.);
            data.extend([255, 255, 255, (a * a * (3. - 2. * a) * 255.).round() as u8]);
        }
    }
    let mut image = Image::new(
        Extent3d {
            width: 64,
            height: 64,
            depth_or_array_layers: 1,
        },
        TextureDimension::D2,
        data,
        TextureFormat::Rgba8UnormSrgb,
        RenderAssetUsages::default(),
    );
    image.sampler = ImageSampler::linear();
    image
}
#[allow(clippy::too_many_arguments)]
fn render(
    mut commands: Commands,
    switches: Res<Switches>,
    state: Res<ViewmodelState>,
    catalog: Res<ItemCatalog>,
    mut visuals: ResMut<Visuals>,
    camera: Query<(&GlobalTransform, &Projection), (With<PlayerCamera>, Without<LightVisual>)>,
    vm: Query<&GlobalTransform, (With<ViewmodelCamera>, Without<LightVisual>)>,
    rail: Query<&GlobalTransform, (With<RailEmitter>, Without<LightVisual>)>,
    context: ReadRapierContext,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
    mut nodes: Query<(&mut Transform, &mut GlobalTransform, &mut Visibility), With<LightVisual>>,
    perspective: Option<Res<crate::avatar::Perspective>>,
    body_muzzle: Query<&crate::avatar::AvatarMuzzle>,
) {
    let Ok((eye, projection)) = camera.single() else {
        return;
    };
    let held = catalog.items.get(&state.held.unwrap_or(0));
    let specs = [
        (
            switches.tactical_lit && switches.tactical_id == 152,
            torch(&catalog),
        ),
        (
            switches.held_lit,
            spot(held.map_or(&Value::Null, |i| &i.melee)),
        ),
        (switches.head_lit, headlamp()),
    ];
    for (i, (on, spec)) in specs.into_iter().enumerate() {
        if visuals.lamps[i].is_none() && on {
            visuals.lamps[i] = Some(
                commands
                    .spawn((
                        Name::new(
                            [
                                "Tactical rail light",
                                "Handheld flashlight",
                                "Worn headlamp",
                            ][i],
                        ),
                        LightVisual,
                        Transform::IDENTITY,
                        Visibility::Hidden,
                    ))
                    .id(),
            );
        }
        if let Some(entity) = visuals.lamps[i] {
            commands.entity(entity).insert(SpotLight {
                color: spec.color,
                range: spec.range,
                intensity: spec.energy * crate::projectiles::effects::light_unit(),
                outer_angle: spec.cone,
                inner_angle: 0.,
                shadow_maps_enabled: false,
                ..default()
            });
            set_pose(
                &mut commands,
                &mut nodes,
                entity,
                eye.compute_transform(),
                on && spec.enabled,
            );
        }
    }
    let laser = switches.tactical_lit && switches.tactical_id == 151;
    if laser && visuals.beam.is_none() {
        let material = materials.add(StandardMaterial {
            base_color: Color::srgba(2., 0., 0., 0.16),
            unlit: true,
            alpha_mode: AlphaMode::Add,
            cull_mode: None,
            ..default()
        });
        visuals.beam = Some(
            commands
                .spawn((
                    Name::new("Source tactical laser beam"),
                    LightVisual,
                    Mesh3d(meshes.add(Cylinder::new(0.004, 1.).mesh().resolution(6).segments(1))),
                    MeshMaterial3d(material),
                    Transform::IDENTITY,
                    Visibility::Hidden,
                    NotShadowCaster,
                ))
                .id(),
        );
        let material = materials.add(StandardMaterial {
            base_color: Color::srgba(2., 0., 0., 0.95),
            base_color_texture: Some(images.add(soft_disc())),
            unlit: true,
            alpha_mode: AlphaMode::Add,
            cull_mode: None,
            ..default()
        });
        visuals.dot = Some(
            commands
                .spawn((
                    Name::new("Source tactical laser surface dot"),
                    LightVisual,
                    Mesh3d(meshes.add(Rectangle::new(1., 1.))),
                    MeshMaterial3d(material),
                    Transform::IDENTITY,
                    Visibility::Hidden,
                    NotShadowCaster,
                ))
                .id(),
        );
    }
    let origin = eye.translation();
    let viewmodel_emitter_pose = vm
        .single()
        .ok()
        .zip(rail.iter().next())
        .map(|(vm, rail)| eye.to_matrix() * vm.to_matrix().inverse() * rail.to_matrix());
    let viewmodel_emitter = viewmodel_emitter_pose
        .map(|pose| pose.transform_point3(Vec3::ZERO))
        .unwrap_or(origin + eye.rotation() * Vec3::new(0.10, -0.06, -0.35));
    // The source keeps the eye ray in both perspectives; only the visible
    // emitter moves to the third-person body's actual weapon muzzle.
    let body_emitter = perspective
        .filter(|view| !view.first_person)
        .and_then(|_| body_muzzle.single().ok());
    let mut emitter = body_emitter.map_or(viewmodel_emitter, |muzzle| muzzle.0);
    let inspect_ray = state.inspect_time.and_then(|_| {
        body_emitter
            .map(|muzzle| (muzzle.0, muzzle.1))
            .or_else(|| viewmodel_emitter_pose.map(super::inspect::laser_ray))
    });
    let (ray_origin, dir, range) = if let Some((from, direction)) = inspect_ray {
        emitter = from;
        (from, direction, 6.)
    } else {
        (origin, eye.rotation() * Vec3::NEG_Z, 2048.)
    };
    let hit = laser
        .then(|| {
            context.single().ok().and_then(|ctx| {
                ctx.cast_ray(
                    ray_origin,
                    dir,
                    range,
                    true,
                    QueryFilter::default()
                        .exclude_sensors()
                        .groups(CollisionGroups::new(
                            Group::ALL,
                            Group::GROUP_1 | Group::GROUP_4,
                        )),
                )
            })
        })
        .flatten();
    let point = hit.map(|(_, distance)| ray_origin + dir * (distance - 0.05));
    // Inspection keeps a six-metre beam visible even when the gun turns toward
    // the sky. The surface dot still requires a real contact.
    let end = point.or_else(|| (laser && inspect_ray.is_some()).then_some(emitter + dir * range));
    let pose = end.and_then(|p| beam_pose(emitter, p));
    if let Some(beam) = visuals.beam {
        set_pose(
            &mut commands,
            &mut nodes,
            beam,
            pose.unwrap_or_default(),
            pose.is_some(),
        );
    }
    if let Some(dot) = visuals.dot {
        let fov = match projection {
            Projection::Perspective(p) => p.fov,
            _ => 60_f32.to_radians(),
        };
        let at = point.unwrap_or_default();
        let pose_dot = Transform::from_translation(at)
            .with_rotation(eye.rotation())
            .with_scale(Vec3::splat(dot_size(at.distance(origin), fov)));
        set_pose(
            &mut commands,
            &mut nodes,
            dot,
            pose_dot,
            pose.is_some() && point.is_some(),
        );
    }
}
fn set_pose(
    commands: &mut Commands,
    nodes: &mut Query<(&mut Transform, &mut GlobalTransform, &mut Visibility), With<LightVisual>>,
    entity: Entity,
    pose: Transform,
    on: bool,
) {
    let visibility = if on {
        Visibility::Visible
    } else {
        Visibility::Hidden
    };
    if let Ok((mut local, mut global, mut visible)) = nodes.get_mut(entity) {
        *local = pose;
        *global = pose.into();
        *visible = visibility;
    } else {
        commands
            .entity(entity)
            .insert((pose, GlobalTransform::from(pose), visibility));
    }
}
/// Original brightest-colour lens mask, including near-identical extracted texels.
fn emission_mask(source: &Image) -> Option<Image> {
    if !matches!(
        source.texture_descriptor.format,
        TextureFormat::Rgba8UnormSrgb | TextureFormat::Rgba8Unorm
    ) {
        return None;
    }
    let pixels = source.data.as_ref()?;
    let mut best = None;
    let mut brightest = -1.;
    for color in pixels.chunks_exact(4) {
        let luminance = color[0] as f32 / 255. * 0.299
            + color[1] as f32 / 255. * 0.587
            + color[2] as f32 / 255. * 0.114;
        if luminance > brightest {
            brightest = luminance;
            best = Some(color);
        }
    }
    let best = best?;
    let mut result = source.clone();
    let data = result.data.as_mut()?;
    for p in data.chunks_exact_mut(4) {
        let difference = (0..3)
            .map(|i| ((p[i] as f32 - best[i] as f32) / 255.).powi(2))
            .sum::<f32>();
        if difference > 0.02_f32.powi(2) * 3. {
            p.copy_from_slice(&[0, 0, 0, 255]);
        }
    }
    Some(result)
}
fn emission(
    switches: Res<Switches>,
    surfaces: Query<(&LightSurface, &MeshMaterial3d<StandardMaterial>)>,
    mut visuals: ResMut<Visuals>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
) {
    for (surface, handle) in &surfaces {
        let Some(mut material) = materials.get_mut(&handle.0) else {
            continue;
        };
        let Some(texture) = &material.base_color_texture else {
            continue;
        };
        let key = texture.id();
        let mask = if let Some(mask) = visuals.masks.get(&key) {
            Some(mask.clone())
        } else {
            let mask = images
                .get(texture)
                .and_then(emission_mask)
                .map(|image| images.add(image));
            if let Some(mask) = &mask {
                visuals.masks.insert(key, mask.clone());
            }
            mask
        };
        material.emissive_texture = mask;
        material.emissive = LinearRgba::rgb(1., 1., 1.)
            * if switches.surface_lit(surface.0) {
                match surface.0 {
                    151 | 152 => 3.,
                    1199 => 3.2,
                    334 | 1044 => std::env::var("UG_NVGLOW")
                        .ok()
                        .and_then(|s| s.parse::<f32>().ok())
                        .filter(|n| n.is_finite())
                        .unwrap_or(1.4),
                    _ => 3.6,
                }
            } else {
                0.
            };
    }
}
#[cfg(test)]
mod tests;
