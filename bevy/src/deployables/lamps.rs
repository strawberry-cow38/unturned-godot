//! Deployable consumer lamps (src InteractableSpot `Spots`, `Deployable.Spawn`
//! light branch and `_Process` envelope). Lights and the carved bulb lens follow
//! the accepted consumer's solved `Powered`: never a pending request, never a
//! client-local solve. The 1.3 s / 1.1 s envelope and its mid-ramp stutter are
//! the source's own presentation clock.
use super::*;
use crate::{hdr::SourceFalloff, viewmodel::lighting::WorldLight};
use bevy::light::NotShadowCaster;

/// `Deployable.WarmupTime` / `CooldownTime`.
const WARMUP: f32 = 1.3;
const COOLDOWN: f32 = 1.1;
/// `Deployable.LensEmission`.
const LENS_EMISSION: f32 = 6.;
/// `SplitLampLens.BulbReach`.
const BULB_REACH: f32 = 0.42;

#[derive(Clone, Copy, Debug, Default, Deserialize)]
pub(super) struct SourceColor {
    r: f32,
    g: f32,
    b: f32,
}
/// `DeployableDef.DeployLight`, in the flat authored frame of the body.
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Light {
    spot: bool,
    pos: SourceVector,
    #[serde(default)]
    dir: SourceVector,
    range: f32,
    #[serde(default)]
    angle_deg: f32,
    energy: f32,
    color: SourceColor,
    #[serde(default)]
    angle_atten: f32,
}
impl Light {
    fn color(&self) -> Color {
        Color::srgb(self.color.r, self.color.g, self.color.b)
    }
}

/// `SplitLampLens`: the body without the lens, the lens, and its white-hot color.
pub(super) struct Art {
    pub body: Option<Handle<Mesh>>,
    lens: Option<Handle<Mesh>>,
    hot: Color,
}
/// The source predicate: UV centroid in the upper-right palette quadrant AND
/// the position centroid within `BulbReach` of one of the def's lamps.
pub(super) fn split_lens(
    def: &Definition,
    data: &crate::content::ObjData,
) -> Option<(crate::content::ObjData, crate::content::ObjData)> {
    if def.lights.is_empty() {
        return None;
    }
    let lamps: Vec<Vec3> = def.lights.iter().map(|l| Vec3::from(l.pos)).collect();
    let (body, lens) = data.split_where_uv(|p, uv| {
        let uv = (uv[0] + uv[1] + uv[2]) / 3.;
        let pc = (p[0] + p[1] + p[2]) / 3.;
        uv.x > 0.5 && uv.y > 0.5 && lamps.iter().any(|l| pc.distance(*l) <= BULB_REACH)
    });
    (!lens.positions.is_empty()).then_some((body, lens))
}
impl Art {
    pub(super) fn load(
        def: &Definition,
        data: Option<&crate::content::ObjData>,
        meshes: &mut Assets<Mesh>,
    ) -> Option<Self> {
        if def.lights.is_empty() {
            return None;
        }
        let split = data.and_then(|d| split_lens(def, d));
        if data.is_some() && split.is_none() {
            // As the source: a model without lens texels lights, but its bulb cannot glow.
            info!(
                "{}: no lamp-glass texels in the model -- the bulb cannot glow",
                def.name
            );
        }
        let (body, lens) = split.map_or((None, None), |(b, l)| {
            (Some(meshes.add(b.mesh())), Some(meshes.add(l.mesh())))
        });
        Some(Self {
            body,
            lens,
            // Godot Color.Lerp(White, 0.75) on the stored sRGB components.
            hot: {
                let c = def.lights[0].color;
                let lerp = |v: f32| v + (1. - v) * 0.75;
                Color::srgb(lerp(c.r), lerp(c.g), lerp(c.b))
            },
        })
    }
}

/// One accepted consumer body's lamps and its envelope.
#[derive(Component)]
pub(crate) struct Lamps {
    net_id: u32,
    consumer: Option<usize>,
    lights: Vec<(Entity, f32)>,
    lens: Option<Entity>,
    off: Handle<StandardMaterial>,
    lit: Option<Handle<StandardMaterial>>,
    hot: Color,
    pub(crate) level: f32,
    flicker: f32,
    flicker_timer: f32,
    seed: u64,
    pub(crate) shown: bool,
}

/// `Deployable.Spawn`: each light is a child of the stood-up body root at its
/// flat-frame position, off until powered; the lens shares the mesh transform.
pub(super) fn spawn(
    commands: &mut Commands,
    def: &Definition,
    art: &Art,
    root: Entity,
    net_id: u32,
    mesh_transform: Transform,
    off: Handle<StandardMaterial>,
) {
    let mut lights = Vec::new();
    for light in &def.lights {
        let at = Vec3::from(light.pos);
        let mut node = if light.spot {
            // Godot SpotAngle is the half-angle; Basis.LookingAt(Dir, Back) aims -Z.
            commands.spawn((
                SpotLight {
                    color: light.color(),
                    intensity: 0.,
                    range: light.range,
                    outer_angle: light.angle_deg.to_radians(),
                    inner_angle: 0.,
                    shadow_maps_enabled: false,
                    ..default()
                },
                SourceFalloff::new(
                    1.,
                    if light.angle_atten > 0. {
                        light.angle_atten
                    } else {
                        1.
                    },
                ),
                Transform::from_translation(at).looking_to(Vec3::from(light.dir), Vec3::Z),
            ))
        } else {
            commands.spawn((
                PointLight {
                    color: light.color(),
                    intensity: 0.,
                    range: light.range,
                    shadow_maps_enabled: false,
                    ..default()
                },
                SourceFalloff::new(1., 1.),
                Transform::from_translation(at),
            ))
        };
        // The source adds every lamp to "dynlight" so it spills onto the held item.
        node.insert((
            WorldLight,
            Visibility::Hidden,
            ChildOf(root),
            Name::new("DeployLight"),
        ));
        lights.push((node.id(), light.energy));
    }
    let lens = art.lens.as_ref().map(|mesh| {
        commands
            .spawn((
                Name::new("LampLens"),
                Mesh3d(mesh.clone()),
                MeshMaterial3d(off.clone()),
                mesh_transform,
                NotShadowCaster,
                ChildOf(root),
            ))
            .id()
    });
    commands.entity(root).insert(Lamps {
        net_id,
        consumer: def.ports.iter().rposition(|p| p.kind == "Consumer"),
        lights,
        lens,
        off,
        lit: None,
        hot: art.hot,
        level: 0.,
        flicker: 1.,
        flicker_timer: 0.,
        seed: 0x9E37_79B9_7F4A_7C15 ^ u64::from(net_id),
        shown: false,
    });
}

/// Source `_Process` lamp block: energized = !OnFire && consumer Powered.
pub(super) fn update(
    time: Res<Time>,
    snapshot: Res<GameSnapshot>,
    accepted: Res<wires::Accepted>,
    ui: Option<Res<crate::ui::UiState>>,
    mut bodies: Query<&mut Lamps>,
    mut lights: Query<(
        &mut Visibility,
        Option<&mut SpotLight>,
        Option<&mut PointLight>,
    )>,
    mut lens: Query<&mut MeshMaterial3d<StandardMaterial>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
) {
    let dt = if ui.is_some_and(|ui| crate::player::simulation_paused(&ui)) {
        0.
    } else {
        time.delta_secs()
    };
    for mut lamps in &mut bodies {
        let lamps = &mut *lamps;
        let on_fire = snapshot.0["deployables"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|r| r["netId"].as_u64() == Some(u64::from(lamps.net_id)))
            .is_none_or(|r| r["onFire"] == true);
        let energized = !on_fire
            && lamps
                .consumer
                .is_some_and(|index| accepted.powered(lamps.net_id, index));
        let target = if energized { 1. } else { 0. };
        lamps.level = if lamps.level < target {
            (lamps.level + dt / WARMUP).min(target)
        } else {
            (lamps.level - dt / COOLDOWN).max(target)
        };
        if lamps.level > 0.02 && lamps.level < 0.98 {
            lamps.flicker_timer -= dt;
            if lamps.flicker_timer <= 0. {
                let seed = &mut lamps.seed;
                lamps.flicker = if crate::particles::random(seed) < 0.5 {
                    0.2 + crate::particles::random(seed) * 0.5
                } else {
                    1.
                };
                lamps.flicker_timer = 0.035 + crate::particles::random(seed) * 0.07;
            }
        } else {
            lamps.flicker = 1.;
        }
        let shown = lamps.level * lamps.flicker;
        lamps.shown = shown > 0.04;
        for (entity, energy) in &lamps.lights {
            let Ok((mut visibility, spot, point)) = lights.get_mut(*entity) else {
                continue;
            };
            let wanted = if shown > 0.04 {
                Visibility::Inherited
            } else {
                Visibility::Hidden
            };
            if *visibility != wanted {
                *visibility = wanted;
            }
            let intensity = crate::hdr::point_lumens(energy * shown);
            if let Some(mut spot) = spot {
                spot.intensity = intensity;
            }
            if let Some(mut point) = point {
                point.intensity = intensity;
            }
        }
        // The bulb rides the same envelope as its light, not a separate snap.
        if let Some(Ok(mut material)) = lamps.lens.map(|e| lens.get_mut(e)) {
            let glow = shown > 0.02;
            if glow {
                let hot = lamps.hot;
                let lit = lamps.lit.get_or_insert_with(|| {
                    materials.add(StandardMaterial {
                        base_color: hot,
                        emissive: LinearRgba::BLACK,
                        metallic: 0.,
                        perceptual_roughness: 0.4,
                        ..default()
                    })
                });
                if let Some(mut m) = materials.get_mut(&*lit) {
                    m.emissive = hot.to_linear() * (LENS_EMISSION * shown);
                }
                if material.0 != *lit {
                    material.0 = lit.clone();
                }
            } else if material.0 != lamps.off {
                material.0 = lamps.off.clone();
            }
        }
    }
}
