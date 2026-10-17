//! `TrafficLight` on `Traffic_Light_0`: two independently timed heads per mast,
//! each with its own red/amber/green lenses carved off the prop
//! (`ObjMesh.SplitTrafficLensesPerHead`). The phase is a pure function of the
//! shared world clock plus a per-head hash, so every peer that has the world's day
//! agrees with nothing else replicated (joined clients: see `Clock`); losing
//! the accepted mains drops a head to the cabinet battery's
//! breathing flash (amber, or red for a flagged side road), which dies with a
//! stutter after two in-game days. Not interactive, as in the source.
use super::*;
use crate::{content::ObjData, hdr::SourceFalloff, world::WorldConfig};

/// Real US signal timings (`GreenSec`/`AmberSec`/`AllRedSec`).
pub(crate) const GREEN_SEC: f32 = 25.;
pub(crate) const AMBER_SEC: f32 = 4.;
pub(crate) const ALL_RED_SEC: f32 = 2.;
pub(crate) const CYCLE_SEC: f32 = GREEN_SEC + AMBER_SEC + ALL_RED_SEC;
const LENS_EMISSION: f32 = 3.;
const FLASH_HZ: f32 = 0.5;
const BATTERY_DAYS: f64 = 2.;
const DYING_SEC: f64 = 2.5;
const GLOW_FORWARD: f32 = 0.25;
const GLOW_RANGE: f32 = 3.2;
const GLOW_BASE: f32 = 0.6;
/// `_Ready`: red, amber, green.
const COLORS: [Color; 3] = [
    Color::srgb(1., 0.15, 0.12),
    Color::srgb(1., 0.68, 0.10),
    Color::srgb(0.25, 1., 0.35),
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Phase {
    Red,
    Amber,
    Green,
    FlashAmber,
    FlashRed,
    Off,
}
/// `LensIndexFor`: the one map from a phase to the lens it lights.
pub(crate) fn lens_index(p: Phase) -> Option<usize> {
    match p {
        Phase::Red | Phase::FlashRed => Some(0),
        Phase::Amber | Phase::FlashAmber => Some(1),
        Phase::Green => Some(2),
        Phase::Off => None,
    }
}
/// `PhaseAt`: green, then amber, then the all-red overlap.
pub(crate) fn phase_at(t: f64, offset: f32) -> Phase {
    let u = (t + f64::from(offset)).rem_euclid(f64::from(CYCLE_SEC)) as f32;
    if u < GREEN_SEC {
        Phase::Green
    } else if u < GREEN_SEC + AMBER_SEC {
        Phase::Amber
    } else {
        Phase::Red
    }
}
/// `TrafficLight.Make`: a deterministic offset from the prop's world position
/// and the head index, so heads differ and every client agrees.
pub(crate) fn offset(world: Vec3, head: usize) -> f32 {
    let h = (world.x * 12.9898 + world.z * 78.233 + head as f32 * 37.719).sin() * 43758.5453;
    (h - h.floor()) * CYCLE_SEC
}
/// `WorldBuilder.SideRoadKey`: placements.txt coordinates (Z un-negated), 0.1 m.
pub(crate) fn side_road_key(world: Vec3) -> (i32, i32) {
    (
        (world.x * 10.).round_ties_even() as i32,
        (-world.z * 10.).round_ties_even() as i32,
    )
}
pub(crate) fn load_side_roads(text: &str) -> std::collections::HashSet<(i32, i32)> {
    text.lines()
        .filter(|l| !l.is_empty() && !l.starts_with('#'))
        .filter_map(|l| {
            let mut f = l.split_whitespace();
            let x: f32 = f.next()?.parse().ok()?;
            let z: f32 = f.next()?.parse().ok()?;
            Some(((x * 10.).round_ties_even() as i32, (z * 10.).round_ties_even() as i32))
        })
        .collect()
}

/// `SplitTrafficLenses` then the per-head cut along mesh Y at the largest gap
/// between lens centroids (none above 0.5 m: one head). Returns the heads'
/// {red, amber, green} and the body.
pub(crate) fn split_heads(data: &ObjData) -> Option<(Vec<[Option<ObjData>; 3]>, ObjData)> {
    let up = |uv: [Vec2; 3]| uv.iter().all(|u| u.y < 0.5);
    let lo = |uv: [Vec2; 3]| uv.iter().all(|u| u.y > 0.5);
    let in_u = |uv: [Vec2; 3], a: f32, b: f32| uv.iter().all(|u| u.x >= a && u.x < b);
    let class = |uv: [Vec2; 3]| {
        if up(uv) && in_u(uv, 0.50, 0.75) {
            0
        } else if up(uv) && in_u(uv, 0.75, 1.01) {
            1
        } else if lo(uv) && in_u(uv, 0.50, 1.01) {
            2
        } else {
            3
        }
    };
    let tris = data.positions.len() / 3;
    let uv = |t: usize| [0, 1, 2].map(|k| Vec2::from_array(data.uvs[t * 3 + k]));
    let y = |t: usize| (data.positions[t * 3][1] + data.positions[t * 3 + 1][1] + data.positions[t * 3 + 2][1]) / 3.;
    let mut cents: Vec<f32> = (0..tris).filter(|&t| class(uv(t)) < 3).map(y).collect();
    if cents.len() < 2 {
        return None;
    }
    cents.sort_by(f32::total_cmp);
    let (mut cut, mut best) = (0f32, 0f32);
    for w in cents.windows(2) {
        if w[1] - w[0] > best {
            best = w[1] - w[0];
            cut = (w[1] + w[0]) * 0.5;
        }
    }
    let heads = if best < 0.5 { 1 } else { 2 };
    let empty = || ObjData {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
        ..default()
    };
    let mut out: Vec<[ObjData; 3]> = (0..heads).map(|_| [empty(), empty(), empty()]).collect();
    let mut body = empty();
    for t in 0..tris {
        let c = class(uv(t));
        let target = if c == 3 {
            &mut body
        } else {
            let h = if heads == 2 && y(t) >= cut { 1 } else { 0 };
            &mut out[h][c]
        };
        for i in t * 3..t * 3 + 3 {
            let p = Vec3::from_array(data.positions[i]);
            target.positions.push(data.positions[i]);
            target.normals.push(data.normals[i]);
            target.uvs.push(data.uvs[i]);
            target.colors.push(data.colors[i]);
            target.min = target.min.min(p);
            target.max = target.max.max(p);
        }
    }
    Some((
        out.into_iter()
            .map(|h| h.map(|d| (!d.positions.is_empty()).then_some(d)))
            .collect(),
        body,
    ))
}

/// One signal head.
#[derive(Component, Debug)]
pub(crate) struct Head {
    /// Which head on the mast, low to high.
    pub(crate) index: usize,
    pub(crate) offset: f32,
    pub(crate) side_road: bool,
    pub(crate) powered: bool,
    battery_dead_at: f64,
    pub(crate) phase: Phase,
    pub(crate) level: f32,
    /// Placement index, the stable key for the persisted battery deadline.
    pub(crate) placement: usize,
    lenses: [Option<Entity>; 3],
    off: Handle<StandardMaterial>,
    glow: Entity,
    stutter_t: f32,
    stutter_show: bool,
    seed: u64,
    shown: Option<(Option<usize>, u8)>,
    /// `Frozen` (`ForcePhase`, the `--trafficlight` harness).
    pub(crate) frozen: bool,
}
impl Head {
    #[cfg(test)]
    pub(crate) fn for_test(offset: f32, side_road: bool) -> Self {
        Self {
            index: 0,
            offset,
            side_road,
            powered: true,
            battery_dead_at: f64::INFINITY,
            phase: Phase::Red,
            level: 1.,
            placement: 0,
            lenses: [None; 3],
            off: default(),
            glow: Entity::PLACEHOLDER,
            stutter_t: 0.,
            stutter_show: true,
            seed: 7,
            shown: None,
            frozen: false,
        }
    }
    /// `ForcePhase`: pin one aspect and stop the timer.
    pub(crate) fn force(&mut self, phase: Phase, level: f32) {
        self.frozen = true;
        self.phase = phase;
        self.level = level;
    }
    pub(crate) fn lit(&self) -> Option<usize> {
        lens_index(self.phase)
    }
    #[cfg(test)]
    pub(crate) fn battery_dead_at(&self) -> f64 {
        self.battery_dead_at
    }
    /// `SetPowered`: the battery deadline is stamped from the clock.
    pub(crate) fn set_powered(&mut self, on: bool, now: f64, day_length: f64) {
        if self.powered == on {
            return;
        }
        self.powered = on;
        self.battery_dead_at = if on {
            f64::INFINITY
        } else {
            now + BATTERY_DAYS * day_length
        };
    }
    /// `HubTick`: the aspect and its continuous level for this clock.
    pub(crate) fn tick(&mut self, t: f64, dt: f32) {
        if self.frozen {
            return;
        }
        let flash = if self.side_road { Phase::FlashRed } else { Phase::FlashAmber };
        let (phase, level) = if self.powered {
            (phase_at(t, self.offset), 1.)
        } else if t >= self.battery_dead_at + DYING_SEC {
            (Phase::Off, 1.)
        } else if t >= self.battery_dead_at {
            // The battery is failing: stutter out, odds ramping toward dark.
            self.stutter_t -= dt;
            if self.stutter_t <= 0. {
                let frac = ((t - self.battery_dead_at) / DYING_SEC).clamp(0., 1.) as f32;
                self.stutter_show = crate::particles::random(&mut self.seed) > frac;
                self.stutter_t = 0.04 + crate::particles::random(&mut self.seed) * 0.08;
            }
            (flash, if self.stutter_show { 1. } else { 0. })
        } else {
            // The breathing blink: a smoothstepped raised cosine at 0.5 Hz.
            let u = 0.5 - 0.5 * (std::f64::consts::TAU * f64::from(FLASH_HZ) * t).cos() as f32;
            (flash, u * u * (3. - 2. * u))
        };
        self.phase = phase;
        if (level - self.level).abs() > 0.01 || self.shown.is_none() {
            self.level = level;
        }
    }
}

#[derive(Resource, Default)]
pub(crate) struct SideRoads(Option<std::collections::HashSet<(i32, i32)>>);

#[allow(clippy::too_many_arguments)]
pub(crate) fn attach(
    mut commands: Commands,
    config: Res<WorldConfig>,
    origin: Option<Res<WorldOrigin>>,
    mut side_roads: ResMut<SideRoads>,
    state: Res<DeviceState>,
    mut cache: ResMut<crate::content::ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut built: Local<Option<Option<(Vec<[Option<(Handle<Mesh>, ObjData)>; 3]>, Handle<Mesh>)>>>,
    objects: Query<
        (Entity, &WorldObject, &Transform, &MeshMaterial3d<StandardMaterial>),
        (Added<WorldObject>, With<PlacementEuler>),
    >,
) {
    for (entity, object, xf, material) in &objects {
        if object.asset_name != "Traffic_Light_0" {
            continue;
        }
        let Some((heads, body)) = built
            .get_or_insert_with(|| {
                let path = config.content_root.join("objects/Traffic_Light_0.obj");
                let (_, data) = cache.load_mesh(&path, &mut meshes).ok()?;
                let (heads, body) = split_heads(&data)?;
                Some((
                    heads
                        .into_iter()
                        .map(|h| h.map(|d| d.map(|d| (meshes.add(d.mesh()), d))))
                        .collect(),
                    meshes.add(body.mesh()),
                ))
            })
            .clone()
        else {
            continue;
        };
        let sides = side_roads.0.get_or_insert_with(|| {
            std::fs::read_to_string(config.content_root.join("objects/traffic_side_roads.txt"))
                .map(|t| load_side_roads(&t))
                .unwrap_or_default()
        });
        let world = absolute(origin.as_deref(), xf.translation);
        let side_road = sides.contains(&side_road_key(world));
        commands.entity(entity).insert(Mesh3d(body));
        let inverse = xf.compute_affine().inverse();
        for (h, lenses) in heads.iter().enumerate() {
            let mut ids = [None; 3];
            let (mut centre, mut forward, mut seen) = (Vec3::ZERO, Vec3::ZERO, 0);
            for (i, lens) in lenses.iter().enumerate() {
                let Some((mesh, data)) = lens else { continue };
                ids[i] = Some(
                    commands
                        .spawn((
                            Name::new("Signal lens"),
                            Mesh3d(mesh.clone()),
                            MeshMaterial3d(material.0.clone()),
                            bevy::light::NotShadowCaster,
                            Transform::IDENTITY,
                            ChildOf(entity),
                        ))
                        .id(),
                );
                centre += xf.transform_point((data.min + data.max) * 0.5);
                let sum: Vec3 = data.normals.iter().map(|n| Vec3::from_array(*n)).sum();
                if sum.length_squared() >= 1e-9 {
                    forward += (xf.rotation * sum.normalize()).normalize();
                }
                seen += 1;
            }
            // The emitter floats 0.25 m in front of the glass, along the lenses' own normals.
            let glow_world = if seen > 0 {
                let f = forward / seen as f32;
                centre / seen as f32 + if f.length_squared() > 1e-6 { f.normalize() * GLOW_FORWARD } else { Vec3::ZERO }
            } else {
                xf.translation
            };
            let glow = commands
                .spawn((
                    Name::new("Signal glow"),
                    PointLight {
                        range: GLOW_RANGE,
                        intensity: 0.,
                        shadow_maps_enabled: false,
                        ..default()
                    },
                    SourceFalloff::new(1., 1.),
                    Transform::from_translation(inverse.transform_point3(glow_world)).with_scale(xf.scale.recip()),
                    Visibility::Hidden,
                    ChildOf(entity),
                ))
                .id();
            // A head already on its battery keeps its deadline across a re-stream.
            let battery = state.signal_battery.get(&(object.source_index, h)).copied();
            commands.spawn((
                Name::new("TrafficLight head"),
                Head {
                    index: h,
                    offset: offset(world, h),
                    side_road,
                    powered: battery.is_none(),
                    battery_dead_at: battery.unwrap_or(f64::INFINITY),
                    phase: Phase::Red,
                    level: 1.,
                    placement: object.source_index,
                    lenses: ids,
                    off: material.0.clone(),
                    glow,
                    stutter_t: 0.,
                    stutter_show: true,
                    seed: 0x2545_F491_4F6C_DD1D ^ ((object.source_index as u64) << 1 | h as u64),
                    shown: None,
                    frozen: false,
                },
                ChildOf(entity),
            ));
        }
    }
}

/// `TrafficLight.ClockSeconds`: (Day + Time) x DayLength off the accepted world
/// clock, so every peer computes the same phase; engine time without one.
///
/// Since protocol 80 a joined client's snapshot carries the host's replicated
/// day (System 21, `RemoteEnvironment`). Before the first environment block
/// arrives there is no `day`, and the day is counted locally from the
/// time-of-day wrapping. The battery deadline (two days out) is then still
/// reached, and the phase matches the host's once the replicated day arrives.
#[derive(Debug, Default)]
pub(crate) struct Clock {
    last_time: Option<f64>,
    days: f64,
}
impl Clock {
    pub(crate) fn seconds(&mut self, snapshot: &Value, elapsed: f64) -> (f64, f64) {
        let clock = &snapshot["worldClock"];
        let length = clock["dayLengthSeconds"].as_f64().filter(|l| *l > 0.);
        match (clock["day"].as_f64(), clock["timeOfDay"].as_f64(), length) {
            (Some(day), Some(time), Some(length)) => {
                self.last_time = None;
                self.days = day;
                ((day + time) * length, length)
            }
            (None, Some(time), Some(length)) => {
                // A drop of more than half a day is the clock wrapping past midnight.
                if self.last_time.is_some_and(|last| time < last - 0.5) {
                    self.days += 1.;
                }
                self.last_time = Some(time);
                ((self.days + time) * length, length)
            }
            _ => (elapsed, 120.),
        }
    }
}

/// The lit lens materials, shared by every head: one per colour and level step,
/// built once and never mutated, so a breathing flash only swaps handles.
#[derive(Resource, Default)]
pub(crate) struct Palette(HashMap<(usize, u8), Handle<StandardMaterial>>);
/// Brightness steps of a lit lens (the flash's level, quantised).
pub(crate) const LEVEL_STEPS: f32 = 32.;
pub(crate) fn level_step(level: f32) -> u8 {
    (level.clamp(0., 1.) * LEVEL_STEPS).round() as u8
}
impl Palette {
    fn get(&mut self, materials: &mut Assets<StandardMaterial>, lens: usize, step: u8) -> Handle<StandardMaterial> {
        self.0
            .entry((lens, step))
            .or_insert_with(|| {
                let c = COLORS[lens];
                materials.add(StandardMaterial {
                    base_color: c,
                    emissive: c.to_linear() * (LENS_EMISSION * f32::from(step) / LEVEL_STEPS),
                    metallic: 0.,
                    perceptual_roughness: 0.4,
                    ..default()
                })
            })
            .clone()
    }
    #[cfg(test)]
    pub(crate) fn len(&self) -> usize {
        self.0.len()
    }
}

/// The mains edge (`DriveStreetlights` -> `SetPowered`), `HubTick` and `Apply`.
#[allow(clippy::too_many_arguments)]
#[allow(clippy::too_many_arguments)]
pub(crate) fn tick(
    time: Res<Time>,
    snapshot: Res<GameSnapshot>,
    mains: Res<Mains>,
    mut state: ResMut<DeviceState>,
    mut palette: ResMut<Palette>,
    mut heads: Query<&mut Head>,
    mut lenses: Query<&mut MeshMaterial3d<StandardMaterial>>,
    mut lights: Query<(&mut PointLight, &mut Visibility)>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let (now, day_length) = state.signal_clock.seconds(&snapshot.0, time.elapsed_secs_f64());
    for mut head in &mut heads {
        let head = &mut *head;
        if !head.frozen {
            head.set_powered(mains.live, now, day_length);
            let key = (head.placement, head.index);
            if head.powered {
                state.signal_battery.remove(&key);
            } else {
                state.signal_battery.insert(key, head.battery_dead_at);
            }
        }
        head.tick(now, time.delta_secs());
        let lit = head.lit();
        let step = level_step(head.level);
        if head.shown == Some((lit, step)) {
            continue;
        }
        head.shown = Some((lit, step));
        for (i, lens) in head.lenses.iter().enumerate() {
            if let Some(Ok(mut m)) = lens.map(|e| lenses.get_mut(e)) {
                let want = if Some(i) == lit { palette.get(&mut materials, i, step) } else { head.off.clone() };
                if m.0 != want {
                    m.0 = want;
                }
            }
        }
        if let Ok((mut light, mut visibility)) = lights.get_mut(head.glow) {
            *visibility = if lit.is_some() { Visibility::Inherited } else { Visibility::Hidden };
            if let Some(i) = lit {
                light.color = COLORS[i];
                light.intensity = crate::hdr::point_lumens(GLOW_BASE * head.level);
            }
        }
    }
}
