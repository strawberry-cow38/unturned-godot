//! `LampLight` (with its `GridLight` base) on the retail indoor fixtures, and
//! the `Deployable` world-scenery lamp `WorldBuilder.PlaceObject` stands in for
//! an upright desk/standing lamp.
//!
//! - `Light_0` ceiling strips: a grid-fed `LampLight`, no manual switch.
//! - `Lamp_0`/`Lamp_1` placed upright (ex 270, ez 0): `Deployable.Spawn` with
//!   `WorldScenery`. Its fixture lamp is fed only by its own consumer port, which
//!   no wire can reach (the server never registered it), so it stays dark.
//! - `Lamp_0`/`Lamp_1` placed tilted: a grid-fed `LampLight` plus the
//!   `LampLight.LookMeta` look target (F toggles `_on`, white outline).
use super::*;
use crate::{
    content::{ContentCache, ObjData},
    hdr::SourceFalloff,
    viewmodel::lighting::WorldLight,
    world::WorldConfig,
};
use bevy::audio::{SpatialScale, Volume};

/// `LampLight.BulbColor`: warm white.
pub(crate) const BULB_COLOR: Color = Color::srgb(1., 0.90, 0.72);
/// `LampLight.Range`.
pub(crate) const RANGE: f32 = 8.;
/// `LampLight.Energy`.
pub(crate) const ENERGY: f32 = 2.2;
/// `LampLight.FixtureEmission`.
pub(crate) const FIXTURE_EMISSION: f32 = 0.9;
/// `GridLight.ReactionMax`.
pub(crate) const REACTION_MAX: f32 = 2.2;
/// `LampLight.DebugBulbSide` default: the +X-facing head opening.
const BULB_SIDE: f32 = 1.;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub(crate) enum Kind {
    Generic,
    CeilingStrip,
    FloorShade,
    DeskBulb,
    CeilingBulb,
}
/// `LampLight.KindFor`: the one prop-name -> kind table.
pub(crate) fn kind_for(name: &str) -> Kind {
    match name {
        "Light_0" | "Light_1" => Kind::CeilingStrip,
        "Ceiling_Bulb_0" | "Ceiling_Shade_Cone_0" | "Ceiling_Shade_Dome_0" => Kind::CeilingBulb,
        "Lamp_1" => Kind::FloorShade,
        "Lamp_0" => Kind::DeskBulb,
        _ => Kind::Generic,
    }
}
/// `LampLight.IsToggle`: the standing and desk lamps get the outline and F.
pub(crate) fn is_toggle(kind: Kind) -> bool {
    matches!(kind, Kind::FloorShade | Kind::DeskBulb)
}
/// `DeployableDef.PropFixture` for the two prop lamps: DeskLamp 1918 (Lamp_0)
/// and StandingLamp 1255 (Lamp_1).
fn fixture_id(kind: Kind) -> Option<u16> {
    match kind {
        Kind::DeskBulb => Some(1918),
        Kind::FloorShade => Some(1255),
        _ => None,
    }
}
/// `WorldBuilder.PlaceObject`'s deployable branch: exactly ex 270, ez 0 (to
/// 0.5 degrees). A tilted lamp cannot be expressed by a yaw and stays a prop.
pub(crate) fn stands_as_deployable(euler: Vec3) -> bool {
    let wrap = |v: f32, lo: f32, hi: f32| lo + (v - lo).rem_euclid(hi - lo);
    (wrap(euler.x, 0., 360.) - 270.).abs() < 0.5 && wrap(euler.z, -180., 180.).abs() < 0.5
}

/// `GridLight.InitJitter`: brightness wear 0.95..1.05 and the reaction-delay
/// fraction, from two independent float32 hashes of the world position.
pub(crate) fn jitter(pos: Vec3) -> (f32, f32) {
    let h = (pos.x * 12.9898 + pos.z * 78.233).sin() * 43758.5453;
    let worn = 0.95 + (h - h.floor()) * 0.10;
    let h2 = (pos.x * 39.3468 + pos.z * 11.7654).sin() * 24634.6345;
    (worn, h2 - h2.floor())
}

fn partition(data: &ObjData, pick: impl Fn(usize) -> bool) -> (ObjData, ObjData) {
    let empty = || ObjData {
        min: Vec3::splat(f32::INFINITY),
        max: Vec3::splat(f32::NEG_INFINITY),
        ..default()
    };
    let (mut body, mut region) = (empty(), empty());
    for t in 0..data.positions.len() / 3 {
        let out = if pick(t) { &mut region } else { &mut body };
        for i in t * 3..t * 3 + 3 {
            push(out, data.positions[i], data.normals[i], data.uvs[i], data.colors[i]);
        }
    }
    (body, region)
}
fn push(out: &mut ObjData, p: [f32; 3], n: [f32; 3], uv: [f32; 2], c: [f32; 4]) {
    out.positions.push(p);
    out.normals.push(n);
    out.uvs.push(uv);
    out.colors.push(c);
    out.min = out.min.min(Vec3::from_array(p));
    out.max = out.max.max(Vec3::from_array(p));
}

/// The emitter split per kind (`LampLight.BuildVisual`). `None` when the
/// predicate selects nothing: the fixture stays whole, as the source does.
pub(crate) fn split(kind: Kind, data: &ObjData) -> Option<(ObjData, ObjData)> {
    let uv = |t: usize, k: usize| Vec2::from_array(data.uvs[t * 3 + k]);
    let pos = |t: usize, k: usize| Vec3::from_array(data.positions[t * 3 + k]);
    let nrm = |t: usize, k: usize| Vec3::from_array(data.normals[t * 3 + k]);
    let (body, mut emit) = match kind {
        // ObjMesh.SplitLens: all three corners in the u>0.5, v>0.5 quadrant.
        Kind::CeilingStrip | Kind::CeilingBulb => {
            partition(data, |t| (0..3).all(|k| uv(t, k).x > 0.5 && uv(t, k).y > 0.5))
        }
        // SplitByUvCentroid(uv.X < 0.5 && uv.Y > 0.5): the 181-grey shade texel.
        Kind::FloorShade => partition(data, |t| {
            let c = (uv(t, 0) + uv(t, 1) + uv(t, 2)) / 3.;
            c.x < 0.5 && c.y > 0.5
        }),
        // SplitByFace: on the head, facing down and to the picked side.
        Kind::DeskBulb => partition(data, |t| {
            let c = (pos(t, 0) + pos(t, 1) + pos(t, 2)) / 3.;
            let n = (nrm(t, 0) + nrm(t, 1) + nrm(t, 2)).normalize_or_zero();
            c.z > 0.6 && n.z < -0.4 && n.x * BULB_SIDE > 0.
        }),
        Kind::Generic => return None,
    };
    if emit.positions.is_empty() {
        return None;
    }
    if matches!(kind, Kind::CeilingStrip | Kind::CeilingBulb) {
        cap_holes(&mut emit);
    }
    Some((body, emit))
}

/// `ObjMesh.CapHoles`: close each boundary loop of the carved lens with a fan
/// wound against the boundary walk. Keys are `Mathf.Round(p * 10000)` (ties to
/// even); the walk order follows .NET's insertion-ordered Dictionary/HashSet.
pub(crate) fn cap_holes(lens: &mut ObjData) {
    type Key = (i32, i32, i32);
    let key = |p: [f32; 3]| {
        let r = |v: f32| (v * 10000.).round_ties_even() as i32;
        (r(p[0]), r(p[1]), r(p[2]))
    };
    let mut pos: HashMap<Key, [f32; 3]> = HashMap::new();
    let mut uvs: HashMap<Key, [f32; 2]> = HashMap::new();
    let mut dir: Vec<(Key, Key)> = Vec::new();
    let mut dir_set = std::collections::HashSet::new();
    for t in 0..lens.positions.len() / 3 {
        for k in 0..3 {
            let a = lens.positions[t * 3 + k];
            let b = lens.positions[t * 3 + (k + 1) % 3];
            let (ka, kb) = (key(a), key(b));
            pos.insert(ka, a);
            uvs.insert(ka, lens.uvs[t * 3 + k]);
            if dir_set.insert((ka, kb)) {
                dir.push((ka, kb));
            }
        }
    }
    // An interior edge is walked once in each direction; a boundary edge once.
    let mut open: Vec<(Key, Key)> = Vec::new();
    let mut open_index: HashMap<Key, usize> = HashMap::new();
    for &(a, b) in &dir {
        if !dir_set.contains(&(b, a)) && !open_index.contains_key(&a) {
            open_index.insert(a, open.len());
            open.push((a, b));
        }
    }
    let mut removed = vec![false; open.len()];
    let mut remaining = open.len();
    let mut guard = 0;
    while remaining > 0 && guard < 64 {
        guard += 1;
        let first = removed.iter().position(|r| !r).unwrap();
        let mut walk = vec![];
        let mut cur = open[first].0;
        while let Some(&i) = open_index.get(&cur) {
            if removed[i] {
                break;
            }
            walk.push(cur);
            removed[i] = true;
            remaining -= 1;
            cur = open[i].1;
            if cur == walk[0] {
                break;
            }
        }
        if walk.len() < 3 {
            continue;
        }
        for i in 1..walk.len() - 1 {
            let corners = [walk[0], walk[i + 1], walk[i]];
            let t = corners.map(|k| Vec3::from_array(pos[&k]));
            let normal = (t[1] - t[0]).cross(t[2] - t[0]).normalize_or_zero();
            for (k, c) in corners.iter().enumerate() {
                push(lens, t[k].to_array(), normal.to_array(), uvs[c], [1.; 4]);
            }
        }
    }
}

/// The carved meshes for one prop, shared by every placement of it.
#[derive(Clone)]
pub(crate) struct Built {
    body: Handle<Mesh>,
    emit: Handle<Mesh>,
    body_data: ObjData,
    emit_data: ObjData,
}

/// `LampLight.ComputeLightLocal`, in world space: where the omni sits.
pub(crate) fn light_world(
    kind: Kind,
    xf: &Transform,
    body: &ObjData,
    emit: Option<&ObjData>,
    lamp_node: Vec3,
) -> Vec3 {
    fn corners(xf: &Transform, d: &ObjData) -> [Vec3; 8] {
        std::array::from_fn(|i| {
            xf.transform_point(Vec3::new(
                if i & 1 == 0 { d.min.x } else { d.max.x },
                if i & 2 == 0 { d.min.y } else { d.max.y },
                if i & 4 == 0 { d.min.z } else { d.max.z },
            ))
        })
    }
    let center = |d: &ObjData| (d.min + d.max) * 0.5;
    match kind {
        Kind::CeilingStrip => {
            // FLOATING well below the prop: the body's lowest world corner - 0.9.
            let c = xf.transform_point(center(body));
            let bottom = corners(xf, body).iter().map(|p| p.y).fold(f32::INFINITY, f32::min) - 0.9;
            Vec3::new(c.x, bottom, c.z)
        }
        Kind::CeilingBulb | Kind::FloorShade => emit.map_or_else(
            || xf.transform_point(center(body)),
            |e| xf.transform_point(center(e)),
        ),
        Kind::DeskBulb => {
            let Some(e) = emit else { return lamp_node };
            let sum: Vec3 = e.normals.iter().map(|n| Vec3::from_array(*n)).sum();
            let average = if sum.length() > 0.001 { sum.normalize() } else { Vec3::NEG_Y };
            let normal = (xf.rotation * (xf.scale * average)).normalize_or_zero();
            xf.transform_point(center(e)) + normal * 0.18
        }
        Kind::Generic => lamp_node,
    }
}

/// `GridLight` presentation state: the grid layer (powered, reaction delay and
/// stutter) and `LampLight._on` on top of it.
#[derive(Clone, Debug)]
pub(crate) struct Grid {
    pub(crate) powered: bool,
    pub(crate) shown_lit: bool,
    /// `_lastLit`: the grid-lit state as last applied (flicker frames included).
    pub(crate) last_lit: bool,
    transitioning: bool,
    target_lit: bool,
    trans_t: f32,
    flick_t: f32,
    reaction: f32,
    reaction_frac: f32,
    seed: u64,
}
impl Grid {
    pub(crate) fn new(powered: bool, reaction_frac: f32, seed: u64) -> Self {
        Self {
            powered,
            shown_lit: powered,
            last_lit: powered,
            transitioning: false,
            target_lit: powered,
            trans_t: 0.,
            flick_t: 0.,
            reaction: 0.,
            reaction_frac,
            seed: seed | 1,
        }
    }
    pub(crate) fn transitioning(&self) -> bool {
        self.transitioning
    }
    /// `SetPowered(on, animate: true)` from the DayNightCycle sweep.
    pub(crate) fn set_powered(&mut self, on: bool) {
        if self.powered == on {
            return;
        }
        self.powered = on;
        // BeginTransition: no visible change and nothing in flight -> nothing.
        let target = self.powered;
        if target == self.shown_lit && !self.transitioning {
            return;
        }
        self.target_lit = target;
        if !self.transitioning {
            self.transitioning = true;
            self.trans_t = 0.;
            self.flick_t = 0.;
            self.reaction = REACTION_MAX * (0.15 + 0.85 * self.reaction_frac);
        }
    }
    /// `GridLight._Process`: blink toward the target, then settle (`Refresh`).
    pub(crate) fn step(&mut self, dt: f32) {
        if !self.transitioning {
            return;
        }
        self.trans_t += dt;
        self.flick_t -= dt;
        if self.flick_t <= 0. {
            let p = (self.trans_t / self.reaction.max(0.05)).clamp(0., 1.);
            let show = crate::particles::random(&mut self.seed) < p;
            self.last_lit = if show { self.target_lit } else { !self.target_lit };
            self.flick_t = 0.04 + crate::particles::random(&mut self.seed) * 0.08;
        }
        if self.trans_t >= self.reaction {
            self.transitioning = false;
            self.shown_lit = self.powered;
            self.last_lit = self.powered;
        }
    }
}

/// One built `LampLight`.
#[derive(Component)]
pub(crate) struct LampFixture {
    pub(crate) kind: Kind,
    pub(crate) source_index: usize,
    pub(crate) grid: Grid,
    pub(crate) on: bool,
    pub(crate) omni_energy: f32,
    pub(crate) light: Entity,
    emissive: Option<Entity>,
    off: Handle<StandardMaterial>,
    lit: Handle<StandardMaterial>,
    pub(crate) light_world: Vec3,
    /// The `LampLight` node (full mesh AABB centre), prop-local: the hum's seat.
    node_local: Vec3,
    applied: Option<(bool, bool)>,
}
impl LampFixture {
    /// `ApplyEffective`: grid-lit AND the manual switch.
    pub(crate) fn effective(&self) -> bool {
        self.grid.last_lit && self.on
    }
}

/// An upright desk/standing lamp's world-scenery `Deployable` readout.
#[derive(Component, Clone)]
pub(crate) struct SceneryLamp {
    pub(crate) name: String,
    pub(crate) health: f32,
}
impl SceneryLamp {
    /// `Deployable._Process` billboard for scenery: the def name with no
    /// runtime suffix, a full HP bar (no backing item), no fuel or load bar,
    /// and no prompt (no toggle, and `WorldScenery && NetId == 0` offers no
    /// pickup). `InfoH`: 0.5 m above the body origin.
    pub(crate) fn billboard(&self, origin: Vec3) -> crate::info_billboard::View {
        crate::deployables::scenery_billboard(&self.name, self.health, origin)
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn attach(
    mut commands: Commands,
    config: Res<WorldConfig>,
    origin: Option<Res<WorldOrigin>>,
    mains: Res<Mains>,
    state: Res<DeviceState>,
    catalog: Res<ItemCatalog>,
    mut cache: ResMut<ContentCache>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut built: Local<MeshCache>,
    objects: Query<
        (
            Entity,
            &WorldObject,
            &Transform,
            Option<&PlacementEuler>,
            Option<&MeshMaterial3d<StandardMaterial>>,
        ),
        Added<WorldObject>,
    >,
) {
    for (entity, object, xf, euler, material) in &objects {
        let kind = kind_for(&object.asset_name);
        // Retail placements only: finite maps place props through EditorObjects.
        let (Kind::CeilingStrip | Kind::FloorShade | Kind::DeskBulb, Some(euler)) = (kind, euler)
        else {
            continue;
        };
        let index = object.source_index;
        if let Some(def) = fixture_id(kind)
            .filter(|_| stands_as_deployable(euler.0))
            .and_then(|id| crate::deployables::fixture_name_health(&catalog, id, &object.asset_name))
        {
            commands.entity(entity).insert((
                Device(Target::SceneryLamp(index)),
                SceneryLamp {
                    name: def.0,
                    health: def.1,
                },
            ));
            continue;
        }
        let Some(material) = material else { continue };
        let path = config.content_root.join(format!("objects/{}.obj", object.asset_name));
        let Ok((_, data)) = cache.load_mesh(&path, &mut meshes) else {
            continue;
        };
        // LampLight.Make at the full mesh's AABB centre: the jitter hashes it.
        let node = xf.transform_point((data.min + data.max) * 0.5);
        let jitter_at = absolute(origin.as_deref(), node);
        build(
            &mut commands,
            &mut meshes,
            &mut materials,
            &mut built,
            Fixture {
                entity,
                kind,
                index,
                name: &object.asset_name,
                xf: *xf,
                data: &data,
                off: material.0.clone(),
                node,
                jitter_at,
                powered: mains.live,
                on: !state.lamp_off.contains(&index),
            },
        );
    }
}

/// What `LampLight.Make` + `BuildVisual` need for one fixture.
pub(crate) struct Fixture<'a> {
    pub entity: Entity,
    pub kind: Kind,
    pub index: usize,
    pub name: &'a str,
    pub xf: Transform,
    pub data: &'a ObjData,
    pub off: Handle<StandardMaterial>,
    /// The `LampLight` node's world position (`LampLight.Make(worldPos)`).
    pub node: Vec3,
    /// That position in absolute source coordinates, for `InitJitter`.
    pub jitter_at: Vec3,
    pub powered: bool,
    pub on: bool,
}
/// `LampLight.BuildVisual`: carve the emitter, the lit material, the omni.
pub(crate) fn build(
    commands: &mut Commands,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    built: &mut MeshCache,
    f: Fixture,
) {
    let Fixture { entity, kind, index, name, xf, data, off, node, jitter_at, powered, on } = f;
    let pieces = built
        .0
        .entry((name.to_owned(), kind as u8))
        .or_insert_with(|| {
            split(kind, data).map(|(body, emit)| Built {
                body: meshes.add(body.mesh()),
                emit: meshes.add(emit.mesh()),
                body_data: body,
                emit_data: emit,
            })
        })
        .clone();
    let (worn, reaction_frac) = jitter(jitter_at);
    // Master: the desk lamp at half intensity.
    let omni_energy = ENERGY * worn * if kind == Kind::DeskBulb { 0.5 } else { 1. };
    // The lit material keeps the albedo/texture and adds warm emission.
    let mut lit = materials.get(&off).cloned().unwrap_or(StandardMaterial {
        base_color: BULB_COLOR,
        ..default()
    });
    lit.emissive = BULB_COLOR.to_linear() * (FIXTURE_EMISSION * worn);
    let lit = materials.add(lit);
    let world = light_world(
        kind,
        &xf,
        pieces.as_ref().map_or(data, |p| &p.body_data),
        pieces.as_ref().map(|p| &p.emit_data),
        node,
    );
    let inverse = xf.compute_affine().inverse();
    let emissive = pieces.as_ref().map(|p| {
        commands.entity(entity).insert(Mesh3d(p.body.clone()));
        commands
            .spawn((
                Name::new("Emissive"),
                Mesh3d(p.emit.clone()),
                MeshMaterial3d(off.clone()),
                Transform::IDENTITY,
                ChildOf(entity),
            ))
            .id()
    });
    let light = commands
        .spawn((
            Name::new("LampLight omni"),
            PointLight {
                color: BULB_COLOR,
                intensity: 0.,
                range: RANGE,
                shadow_maps_enabled: false,
                ..default()
            },
            SourceFalloff::new(1., 1.),
            Transform::from_translation(inverse.transform_point3(world))
                .with_scale(xf.scale.recip()),
            Visibility::Hidden,
            ChildOf(entity),
        ))
        .id();
    commands.entity(entity).insert(LampFixture {
        kind,
        source_index: index,
        grid: Grid::new(powered, reaction_frac, 0x9E37_79B9_7F4A_7C15 ^ index as u64),
        on,
        omni_energy,
        light,
        emissive,
        off,
        lit,
        light_world: world,
        node_local: inverse.transform_point3(node),
        applied: None,
    });
    if is_toggle(kind) {
        commands.entity(entity).insert(Device(Target::Lamp(index)));
    }
}

/// The mains sweep, the transition clock and `ApplyEffective`.
pub(crate) fn grid(
    mut commands: Commands,
    time: Res<Time>,
    mains: Res<Mains>,
    ui: Option<Res<UiState>>,
    mut lamps: Query<&mut LampFixture>,
    mut lights: Query<(&mut PointLight, &mut Visibility)>,
    mut emissive: Query<&mut MeshMaterial3d<StandardMaterial>>,
) {
    let dt = if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        0.
    } else {
        time.delta_secs()
    };
    for mut lamp in &mut lamps {
        let lamp = &mut *lamp;
        lamp.grid.set_powered(mains.live);
        lamp.grid.step(dt);
        let eff = lamp.effective();
        if lamp.applied == Some((eff, lamp.grid.shown_lit)) {
            continue;
        }
        lamp.applied = Some((eff, lamp.grid.shown_lit));
        if let Ok((mut light, mut visibility)) = lights.get_mut(lamp.light) {
            light.intensity = if eff { crate::hdr::point_lumens(lamp.omni_energy) } else { 0. };
            *visibility = if eff { Visibility::Inherited } else { Visibility::Hidden };
        }
        // SyncDynGroup follows the grid's Lit, not the manual switch.
        if lamp.grid.shown_lit {
            commands.entity(lamp.light).insert(WorldLight);
        } else {
            commands.entity(lamp.light).remove::<WorldLight>();
        }
        if let Some(Ok(mut material)) = lamp.emissive.map(|e| emissive.get_mut(e)) {
            material.0 = if eff { lamp.lit.clone() } else { lamp.off.clone() };
        }
    }
}

/// F on a toggle lamp: flip its manual switch (`LampLight.Toggle`). Grid power
/// is still required to emit. Local state, as the source's `_on`.
#[allow(clippy::too_many_arguments)]
pub(crate) fn press(
    ui: Res<UiState>,
    settings: Res<Settings>,
    keys: Res<ButtonInput<KeyCode>>,
    mouse: Res<ButtonInput<MouseButton>>,
    focus: Res<InteractionFocus>,
    mut state: ResMut<DeviceState>,
    mut lamps: Query<(&Device, &mut LampFixture)>,
) {
    for (device, mut lamp) in &mut lamps {
        if pressed_target(&ui, &settings, &keys, &mouse, &focus, device.0) {
            lamp.on = !lamp.on;
            if lamp.on {
                state.lamp_off.remove(&lamp.source_index);
            } else {
                state.lamp_off.insert(lamp.source_index);
            }
        }
    }
}

/// `LampLight` ceiling-strip hum: `fluorescent_hum.wav`, looping, 0.35 linear,
/// UnitSize 2.2, MaxDistance 13; it rides the effective state (hard mute off).
#[derive(Component)]
pub(crate) struct Hum {
    lamp: Entity,
    epoch: u64,
}
const HUM_UNIT: f32 = 2.2;
const HUM_RANGE: f32 = 13.;
#[allow(clippy::too_many_arguments)]
pub(crate) fn hum(
    mut commands: Commands,
    config: Res<WorldConfig>,
    assets: Res<AssetServer>,
    audio: Option<Res<Assets<AudioSource>>>,
    settings: Option<Res<Settings>>,
    epoch: Res<WorldSessionEpoch>,
    listener: Query<&GlobalTransform, With<PlayerCamera>>,
    lamps: Query<(Entity, &LampFixture, &GlobalTransform)>,
    mut voices: Query<(Entity, &Hum, &mut Transform, &mut PlaybackSettings, Option<&mut SpatialAudioSink>)>,
) {
    let master = settings.map_or(1., |s| s.master_volume);
    let Some(ear) = listener.single().ok().map(|t| t.translation()) else {
        for (entity, ..) in &voices {
            commands.entity(entity).try_despawn();
        }
        return;
    };
    let db = 20. * 0.35_f32.log10();
    let mut wanted: HashMap<Entity, Vec3> = lamps
        .iter()
        .filter(|(_, l, _)| l.kind == Kind::CeilingStrip)
        .map(|(e, l, pose)| (e, pose.transform_point(l.node_local), l))
        .filter(|(_, at, _)| at.distance(ear) < HUM_RANGE)
        .map(|(e, at, l)| (e, if l.effective() { at } else { Vec3::NAN }))
        .collect();
    for (entity, voice, mut pose, mut playback, sink) in &mut voices {
        let Some(at) = wanted.remove(&voice.lamp).filter(|_| voice.epoch == epoch.0) else {
            commands.entity(entity).try_despawn();
            continue;
        };
        let gain = if at.is_nan() {
            0.
        } else {
            crate::sound::distance_gain(db, HUM_UNIT, HUM_RANGE, at.distance(ear)) * master
        };
        if !at.is_nan() {
            pose.translation = at;
        }
        playback.volume = Volume::Linear(gain);
        if let Some(mut sink) = sink {
            sink.set_volume(Volume::Linear(gain));
        }
    }
    let path = "sounds/fluorescent_hum.wav";
    if wanted.is_empty() || audio.is_none() || !config.content_root.join(path).is_file() {
        return;
    }
    let clip = assets.load(path);
    for (lamp, at) in wanted {
        commands.spawn((
            Name::new("Fluorescent hum"),
            Hum {
                lamp,
                epoch: epoch.0,
            },
            AudioPlayer::new(clip.clone()),
            PlaybackSettings::LOOP
                .with_spatial(true)
                .with_spatial_scale(SpatialScale::new(0.5 / HUM_RANGE))
                .with_volume(Volume::SILENT),
            Transform::from_translation(if at.is_nan() { Vec3::ZERO } else { at }),
        ));
    }
}
