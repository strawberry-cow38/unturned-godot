//! Original rope tow (Vehicle.AttachTow/UpdateTow, TowRope). The authority owns
//! who is roped and the rest length (bridge `VehicleTowServices`); this applies
//! the pull-only spring to the locally simulated real bodies and draws the rope
//! from the accepted relationship on every client.
use super::*;

/// Source Vehicle tow constants.
pub(crate) const TOW_STIFFNESS: f32 = 20000.;
pub(crate) const TOW_DAMPING: f32 = 3200.;
pub(crate) const TOW_MAX_FORCE: f32 = 30000.;
/// A towed car's wheel friction slip while roped (SetTowedFreeRoll).
pub(crate) const TOWED_FREE_ROLL_SLIP: f32 = 0.5;
const ROPE_RADIUS: f32 = 0.032;
const ROPE_SEGMENTS: usize = 10;

/// Accepted rope relationship: `towed` when this body tows, `by` when towed.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub(crate) struct Tow {
    pub towed: u32,
    pub rest: f32,
    pub by: u32,
}
impl Tow {
    pub(crate) fn roped(&self) -> bool {
        self.towed != 0 || self.by != 0
    }
}

/// Vehicle.Build: bumper-height centre of the front (-Z) or rear (+Z) box face,
/// nudged 0.15 m outside the hull.
pub(crate) fn tow_local(spec: &data::VehicleSpec, rear: bool) -> Vec3 {
    let (c, s) = (Vec3::from_array(spec.box_center), Vec3::from_array(spec.box_size));
    Vec3::new(
        c.x,
        c.y - s.y * 0.30,
        if rear { c.z + s.z * 0.5 + 0.15 } else { c.z - s.z * 0.5 - 0.15 },
    )
}

/// One rope end's physical state for UpdateTow.
#[derive(Clone, Copy)]
pub(crate) struct End {
    pub pose: Transform,
    pub velocity: Vec3,
    pub spec: &'static data::VehicleSpec,
}
impl End {
    fn point(&self, rear: bool) -> Vec3 {
        self.pose.transform_point(tow_local(self.spec, rear))
    }
    pub(crate) fn aircraft(&self) -> bool {
        self.spec.heli || self.spec.plane
    }
}

/// Source UpdateTow tension: the rope only pulls. Returns the force on the
/// TOWER at its rear point (the towed car receives the opposite at its front),
/// or None when slack. The snap past TowBreakLen is authority-owned.
pub(crate) fn tension(tower: &End, towed: &End, rest: f32) -> Option<(Vec3, Vec3, Vec3)> {
    let (a, b) = (tower.point(true), towed.point(false));
    let d = b - a;
    let dist = d.length();
    if dist < 1e-3 || dist <= rest {
        return None;
    }
    let dir = d / dist;
    let separating = (towed.velocity - tower.velocity).dot(dir);
    let f = (TOW_STIFFNESS * (dist - rest) + TOW_DAMPING * separating).clamp(0., TOW_MAX_FORCE);
    Some((dir * f, a, b))
}

/// UpdateTow's `ApplyForce(f, point - ToGlobal(CenterOfMass))`. Godot 4.6
/// (Jolt) reads that position as an offset from the body ORIGIN and takes the
/// torque arm relative to the CoM offset, so the source arm is
/// `point - origin - 2·CoM` (measured: `bevy/tools/apply_force_arm_reference.py`).
/// Ported as observed; Rapier's torque is about the true CoM.
pub(crate) fn apply(force: &mut ExternalForce, end: &End, f: Vec3, at: Vec3) {
    let com = end.pose.rotation * end.spec.center_of_mass().0;
    force.force += f;
    force.torque += (at - end.pose.translation - 2. * com).cross(f);
}

/// Vehicle._towSavedSlip: the towed car's per-wheel slip while it free-rolls.
#[derive(Clone, Debug, Default, PartialEq)]
pub(crate) struct SavedSlip(Option<Vec<f32>>);
impl SavedSlip {
    /// SetTowedFreeRoll(on): save once and hold 0.5; release restores the
    /// saved slips (a tracked or fixed-wing hull never rewrites its own).
    pub(crate) fn free_roll(&mut self, on: bool, wheels: &mut RayCastVehicleController) {
        if on {
            if self.0.is_none() {
                self.0 = Some(wheels.wheels.iter().map(|w| w.tuning.friction_slip).collect());
            }
            for wheel in &mut wheels.wheels {
                wheel.tuning.friction_slip = TOWED_FREE_ROLL_SLIP;
            }
        } else if let Some(saved) = self.0.take() {
            for (wheel, slip) in wheels.wheels.iter_mut().zip(saved) {
                wheel.tuning.friction_slip = slip;
            }
        }
    }
}

#[derive(Component)]
struct RopeSegment;
#[derive(Resource, Default)]
struct Ropes {
    segments: HashMap<u32, Vec<Entity>>,
    art: Option<(Handle<Mesh>, Handle<StandardMaterial>)>,
}

pub(super) fn install(app: &mut App) {
    tool::install(app);
    install_draw_resources(app);
    app.add_systems(PostUpdate, draw.before(TransformSystems::Propagate));
}
pub(crate) fn install_draw_resources(app: &mut App) {
    app.init_resource::<Ropes>();
}

/// TowRope.SetEndpoints: a hemp polyline of ten stretched cylinders that sags
/// as a parabola by half its slack (plus a 6 cm baseline) when not taut.
pub(crate) fn sag_points(a: Vec3, b: Vec3, rest: f32) -> [Vec3; ROPE_SEGMENTS + 1] {
    let sag = (rest - a.distance(b)).max(0.) * 0.5 + 0.06;
    std::array::from_fn(|i| {
        let t = i as f32 / ROPE_SEGMENTS as f32;
        let mut p = a.lerp(b, t);
        p.y -= sag * 4. * t * (1. - t);
        p
    })
}

pub(crate) fn draw(
    mut commands: Commands,
    mut ropes: ResMut<Ropes>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    bodies: Query<(&VehicleBody, &Transform)>,
    vehicles: Res<Vehicles>,
) {
    let (mesh, material) = ropes
        .art
        .get_or_insert_with(|| {
            (
                meshes.add(Cylinder::new(ROPE_RADIUS, 1.).mesh().resolution(6)),
                materials.add(StandardMaterial {
                    base_color: Color::srgb(0.42, 0.30, 0.16),
                    metallic: 0.,
                    perceptual_roughness: 0.9,
                    ..default()
                }),
            )
        })
        .clone();
    let mut live = HashSet::new();
    for (body, pose) in &bodies {
        if body.rope.towed == 0 {
            continue;
        }
        let Some((towed, towed_pose)) = vehicles
            .entities
            .get(&body.rope.towed)
            .and_then(|e| bodies.get(*e).ok())
        else {
            continue;
        };
        let (Some(a), Some(b)) = (data::definition(body.type_id), data::definition(towed.type_id))
        else {
            continue;
        };
        live.insert(body.net_id);
        let points = sag_points(
            pose.transform_point(tow_local(&a.spec, true)),
            towed_pose.transform_point(tow_local(&b.spec, false)),
            body.rope.rest,
        );
        let pool = ropes.segments.entry(body.net_id).or_default();
        while pool.len() < ROPE_SEGMENTS {
            pool.push(
                commands
                    .spawn((
                        Name::new(format!("Tow rope #{}", body.net_id)),
                        RopeSegment,
                        Mesh3d(mesh.clone()),
                        MeshMaterial3d(material.clone()),
                        Transform::default(),
                        Visibility::Hidden,
                    ))
                    .id(),
            );
        }
        for (entity, pair) in pool.iter().zip(points.windows(2)) {
            let pose = crate::deployables::segment_pose(pair[0], pair[1]);
            commands.entity(*entity).insert((
                pose.unwrap_or_default(),
                if pose.is_some() { Visibility::Inherited } else { Visibility::Hidden },
            ));
        }
    }
    ropes.segments.retain(|tower, pool| {
        let keep = live.contains(tower);
        if !keep {
            for entity in pool.drain(..) {
                commands.entity(entity).despawn();
            }
        }
        keep
    });
}

pub(crate) mod tool;
#[cfg(test)]
mod tests;
