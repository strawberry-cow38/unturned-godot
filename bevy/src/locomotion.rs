//! Collision adapter for the original PlayerController swimming and ladder paths.
//! Ground movement remains in PlayerMovementSim; these are the source shell's
//! camera/world-dependent branches, with the same probes and mantle clearances.
use bevy::prelude::*;
use bevy_rapier3d::prelude::*;

#[derive(Default)]
pub struct Locomotion {
    ladder: Option<Entity>,
    cooldown: f32,
    at_top: bool,
    mantle: Option<Mantle>,
}
struct Mantle {
    points: [Vec3; 4],
    distance: f32,
    length: f32,
}
pub struct LadderGeometry {
    pub origin: Vec3,
    pub face: Vec3,
    pub top: f32,
}
pub struct SpecialMove {
    pub velocity: Vec3,
    pub stance: &'static str,
    pub teleported: bool,
}

pub fn climbable(normal: Vec3, face: Vec3) -> bool {
    let Some(n) = normal.try_normalize() else {
        return false;
    };
    let Some(f) = face.try_normalize() else {
        return false;
    };
    n.dot(f).abs() > 0.9 && f.y.abs() <= 0.1
}

pub fn swim_velocity(feet: Vec3, sea: f32, yaw: f32, pitch: f32, axes: Vec2, jump: bool) -> Vec3 {
    let local = Vec3::new(axes.x, 0., -axes.y).clamp_length_max(1.);
    let aim = Quat::from_euler(EulerRot::YXZ, yaw, pitch, 0.);
    let diving = (aim * Vec3::NEG_Z).y < -0.25 && axes.y > 0.1;
    if feet.y + 1.75 < sea || diving {
        let mut velocity = aim * local * 4.5;
        if jump {
            velocity.y = 3.;
        }
        velocity
    } else {
        let horizontal = Quat::from_rotation_y(yaw) * local * 4.5;
        Vec3::new(horizontal.x, (sea - 1.275 - feet.y) / 8., horizontal.z)
    }
}

impl Locomotion {
    pub fn shift_origin(&mut self,shift:Vec3) {
        if let Some(path)=&mut self.mantle {for point in &mut path.points {*point-=shift;}}
    }
    fn detach(&mut self) {
        if self.ladder.is_some() {
            self.cooldown = 0.45;
        }
        self.ladder = None;
        self.at_top = false;
        self.mantle = None;
    }

    #[allow(clippy::too_many_arguments)]
    pub fn step(
        &mut self,
        context: &RapierContext<'_>,
        owner: Entity,
        ladder_geometry: impl Fn(Entity) -> Option<LadderGeometry>,
        feet: &mut Vec3,
        yaw: f32,
        pitch: f32,
        axes: Vec2,
        jump: bool,
        sea: Option<f32>,
        dt: f32,
    ) -> Option<SpecialMove> {
        self.cooldown = (self.cooldown - dt).max(0.);
        if let Some(path) = &mut self.mantle {
            path.distance = (path.distance + 2.6 * dt).min(path.length);
            *feet = along_path(&path.points, path.distance);
            if path.distance >= path.length - 0.0001 {
                self.detach();
            }
            return Some(SpecialMove {
                velocity: Vec3::ZERO,
                stance: "climb",
                teleported: true,
            });
        }
        let underwater = sea.is_some_and(|sea| feet.y + 1.25 < sea);
        // A submerged ladder is only an exit: idle/backwards releases it.
        if !underwater || axes.y > 0.1 {
            let forward = Quat::from_rotation_y(yaw) * Vec3::NEG_Z;
            let from = *feet + Vec3::Y * if self.ladder.is_some() { 0.1 } else { 0.5 };
            let filter = || {
                QueryFilter::default()
                    .exclude_collider(owner)
                    .exclude_sensors()
            };
            let probe = |origin| {
                let (entity, hit) =
                    context.cast_ray_and_get_normal(origin, forward, 0.75, true, filter())?;
                let geometry = ladder_geometry(entity)?;
                climbable(hit.normal, geometry.face).then_some((entity, hit, geometry))
            };
            if let Some((entity, hit, geometry)) = probe(from) {
                self.at_top = false;
                let mut teleported = false;
                if self.ladder.is_none() && self.cooldown <= 0. {
                    let target = Vec3::new(geometry.origin.x, hit.point.y - 0.5, geometry.origin.z)
                        + hit.normal.normalize() * 0.5;
                    if standing_fits(context, owner, target, 0.12, 1.82) {
                        *feet = target;
                        self.ladder = Some(entity);
                        teleported = true;
                    }
                }
                if self.ladder.is_some() {
                    self.ladder = Some(entity);
                    return Some(SpecialMove {
                        velocity: Vec3::Y * axes.y * 2.25,
                        stance: "climb",
                        teleported,
                    });
                }
            } else if let Some((entity, _, geometry)) = self.ladder.and_then(|attached| {
                probe(from - Vec3::Y * 0.45).filter(|(entity, _, _)| *entity == attached)
            }) {
                self.ladder = Some(entity);
                self.at_top = true;
                if axes.y > 0.1 {
                    self.mantle = find_mantle(
                        context,
                        owner,
                        &ladder_geometry,
                        *feet,
                        forward,
                        geometry.top,
                    );
                }
                return Some(SpecialMove {
                    velocity: Vec3::Y * axes.y.min(0.) * 2.25,
                    stance: "climb",
                    teleported: false,
                });
            }
        }
        self.detach();
        underwater.then(|| SpecialMove {
            velocity: swim_velocity(*feet, sea.unwrap(), yaw, pitch, axes, jump),
            stance: "swim",
            teleported: false,
        })
    }
}

fn standing_fits(
    context: &RapierContext<'_>,
    owner: Entity,
    feet: Vec3,
    inset: f32,
    height: f32,
) -> bool {
    let radius = 0.28;
    let shape = Collider::capsule(
        Vec3::Y * (inset + radius),
        Vec3::Y * (inset + height - radius),
        radius,
    );
    let mut blocked = false;
    context.intersect_shape(
        feet,
        Quat::IDENTITY,
        &shape,
        QueryFilter::default()
            .exclude_collider(owner)
            .exclude_sensors(),
        |_, _| {
            blocked = true;
            false
        },
    );
    !blocked
}

fn find_mantle(
    context: &RapierContext<'_>,
    owner: Entity,
    ladders: &impl Fn(Entity) -> Option<LadderGeometry>,
    feet: Vec3,
    forward: Vec3,
    top: f32,
) -> Option<Mantle> {
    for d in [0.95, 1.2, 1.45, 0., -0.4, -0.75] {
        let column = feet + forward * d;
        let origin = Vec3::new(column.x, top + 1.6, column.z);
        let Some((entity, hit)) = context.cast_ray_and_get_normal(
            origin,
            Vec3::NEG_Y,
            origin.y - feet.y + 0.8,
            true,
            QueryFilter::default()
                .exclude_collider(owner)
                .exclude_sensors(),
        ) else {
            continue;
        };
        if ladders(entity).is_some() || hit.normal.y < 0.7 {
            continue;
        }
        let land = hit.point;
        if land.y > feet.y + 1.2 || land.y < feet.y - 0.8 {
            continue;
        }
        let clear_y = land.y.max(if d > 0.5 { top } else { feet.y }) + 0.08;
        let rise = Vec3::new(feet.x, clear_y.max(feet.y), feet.z);
        let across = Vec3::new(land.x, rise.y, land.z);
        let end = land + Vec3::Y * 0.02;
        if [rise, (rise + across) * 0.5, across, end]
            .iter()
            .any(|p| !standing_fits(context, owner, *p, 0.06, 1.9))
        {
            continue;
        }
        let points = [feet, rise, across, end];
        let length = points.windows(2).map(|p| p[0].distance(p[1])).sum();
        return Some(Mantle {
            points,
            length,
            distance: 0.,
        });
    }
    None
}
fn along_path(points: &[Vec3; 4], mut distance: f32) -> Vec3 {
    for pair in points.windows(2) {
        let length = pair[0].distance(pair[1]);
        if distance <= length {
            return if length > 0.00001 {
                pair[0].lerp(pair[1], distance / length)
            } else {
                pair[1]
            };
        }
        distance -= length;
    }
    points[3]
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn swimming_dives_with_aim_and_surfaces_without_sinking_the_head() {
        let feet = Vec3::new(0., -1.275, 0.);
        let surface = swim_velocity(feet, 0., 0., 0., Vec2::Y, false);
        assert_eq!(surface, Vec3::new(0., 0., -4.5));
        let dive = swim_velocity(feet, 0., 0., -0.6, Vec2::Y, false);
        assert!(dive.y < -2.);
        assert!((dive.length() - 4.5).abs() < 0.00001);
        let rise = swim_velocity(Vec3::Y * -3., 0., 0., -0.6, Vec2::Y, true);
        assert_eq!(rise.y, 3.);
        let diagonal = swim_velocity(feet, 0., 0., 0., Vec2::ONE, false);
        assert!((diagonal.length() - 4.5).abs() < 0.00001);
    }
    #[test]
    fn ladder_faces_reject_edges_and_sloped_ladders() {
        assert!(climbable(Vec3::Z, Vec3::Z));
        assert!(climbable(Vec3::NEG_Z, Vec3::Z));
        assert!(!climbable(Vec3::X, Vec3::Z));
        assert!(!climbable(
            Vec3::new(0., 0.5, 0.866),
            Vec3::new(0., 0.5, 0.866)
        ));
        assert!(!climbable(Vec3::ZERO, Vec3::Z));
    }

    #[test]
    fn real_collision_ladder_snaps_once_holds_the_top_and_steps_onto_a_flush_roof() {
        use bevy::ecs::system::SystemState;
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            RapierPhysicsPlugin::<NoUserData>::default(),
        ));
        app.insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ));
        let owner = app.world_mut().spawn(Transform::default()).id();
        let ladder = app
            .world_mut()
            .spawn((
                Collider::cuboid(0.58, 3.38, 0.075),
                Transform::from_xyz(0., 3.38, 0.),
            ))
            .id();
        app.world_mut().spawn((
            Collider::cuboid(10., 0.1, 10.),
            Transform::from_xyz(0., -0.1, 0.),
        ));
        for _ in 0..3 {
            app.update();
        }
        let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
        let mut locomotion = Locomotion::default();
        let mut feet = Vec3::new(0.2, 0., 0.6);
        let geometry = |id| {
            (id == ladder).then_some(LadderGeometry {
                origin: Vec3::new(0., 3.38, 0.),
                face: Vec3::Z,
                top: 6.76,
            })
        };
        {
            let read = state.get(app.world()).unwrap();
            let context = read.single().unwrap();
            let first = locomotion
                .step(
                    &context,
                    owner,
                    geometry,
                    &mut feet,
                    0.,
                    0.,
                    Vec2::Y,
                    false,
                    None,
                    0.02,
                )
                .unwrap();
            assert_eq!(first.stance, "climb");
            assert!(first.teleported);
            assert!(feet.x.abs() < 0.0001);
            assert!((feet.z - 0.5).abs() < 0.0001);
            feet.x = 0.1;
            let next = locomotion
                .step(
                    &context,
                    owner,
                    geometry,
                    &mut feet,
                    0.,
                    0.,
                    Vec2::Y,
                    false,
                    None,
                    0.02,
                )
                .unwrap();
            assert!(!next.teleported);
            assert_eq!(feet.x, 0.1);
            feet.y = 6.72;
            let top = locomotion
                .step(
                    &context,
                    owner,
                    geometry,
                    &mut feet,
                    0.,
                    0.,
                    Vec2::Y,
                    false,
                    None,
                    0.02,
                )
                .unwrap();
            assert_eq!(
                top.velocity,
                Vec3::ZERO,
                "a ladder ending in air must hold the player"
            );
            assert!(locomotion.mantle.is_none());
        }
        app.world_mut().spawn((
            Collider::cuboid(3., 0.1, 2.),
            Transform::from_xyz(0., 6.66, -2.075),
        ));
        for _ in 0..3 {
            app.update();
        }
        let read = state.get(app.world()).unwrap();
        let context = read.single().unwrap();
        locomotion.step(
            &context,
            owner,
            geometry,
            &mut feet,
            0.,
            0.,
            Vec2::Y,
            false,
            None,
            0.02,
        );
        assert!(
            locomotion.mantle.is_some(),
            "flush roof should be reachable over the ladder"
        );
        for _ in 0..50 {
            if locomotion.mantle.is_none() {
                break;
            }
            locomotion.step(
                &context,
                owner,
                geometry,
                &mut feet,
                0.,
                0.,
                Vec2::Y,
                false,
                None,
                0.02,
            );
        }
        assert!(feet.z < -0.2);
        assert!((feet.y - 6.78).abs() < 0.001);
        assert!(locomotion.ladder.is_none());
        assert!(locomotion.cooldown > 0.);
    }
}
