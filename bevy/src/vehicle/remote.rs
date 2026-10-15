//! VehicleReplicaView: wire-only puppets glide toward a bounded extrapolation;
//! the accepted local driver keeps the existing single native physics body.
use super::*;
#[derive(Component, Default)]
pub(super) struct Puppet {
    active: bool,
    last_position: Vec3,
    since_snapshot: f32,
}
impl Puppet {
    fn advance(&mut self, pose: &mut Transform, row: &Replica, dt: f32) -> f32 {
        let target_pose = replica_pose(row);
        if !self.active || target_pose.translation != self.last_position {
            self.last_position = target_pose.translation;
            self.since_snapshot = 0.;
        } else {
            self.since_snapshot += dt;
        }
        self.active = true;
        let velocity = Vec3::from_array(row.lin_vel);
        let target = target_pose.translation + velocity * self.since_snapshot.min(0.5);
        let alpha = 1. - (-12. * dt).exp();
        pose.translation = if pose.translation.distance(target) > 8. {
            target
        } else {
            pose.translation.lerp(target, alpha)
        };
        pose.rotation = pose.rotation.slerp(target_pose.rotation, alpha).normalize();
        if self.since_snapshot < 0.5 {
            velocity.dot(target_pose.rotation * Vec3::NEG_Z)
        } else {
            0.
        }
    }
}
pub(super) fn update(
    mut commands: Commands,
    snapshot: Res<GameSnapshot>,
    time: Res<Time>,
    mut bodies: Query<(
        Entity,
        &mut VehicleBody,
        &mut Transform,
        &mut Velocity,
        &mut RayCastVehicleController,
        Option<&mut Puppet>,
    )>,
) {
    if crate::network::owns_world(&snapshot.0) {
        return;
    }
    let local = snapshot.0["playerId"].as_u64().unwrap_or(0) as u32;
    let rows: Vec<Replica> =
        serde_json::from_value(snapshot.0["vehicles"].clone()).unwrap_or_default();
    for (entity, mut body, mut pose, mut velocity, mut controller, puppet) in &mut bodies {
        let Some(row) = rows.iter().find(|row| row.net_id == body.net_id) else {
            continue;
        };
        let Some(mut puppet) = puppet else {
            commands.entity(entity).insert(Puppet::default());
            continue;
        };
        if row.driver_player_id == local && local != 0 {
            if puppet.active {
                *pose = replica_pose(row);
                velocity.linear = Vec3::from_array(row.lin_vel);
                velocity.angular = Vec3::from_array(row.ang_vel);
                puppet.active = false;
            }
            continue;
        }
        let forward = puppet.advance(&mut pose, row, time.delta_secs());
        velocity.linear = Vec3::from_array(row.lin_vel);
        velocity.angular = Vec3::from_array(row.ang_vel);
        body.drivetrain.steering = row.steer_degrees;
        if let Some(def) = data::definition(body.type_id) {
            for (i, wheel) in controller.wheels.iter_mut().enumerate() {
                wheel.steering = if def.spec.wheels().get(i).is_some_and(|w| w.3) {
                    row.steer_degrees.to_radians()
                } else {
                    0.
                };
                wheel.state.rotation -=
                    forward / def.spec.wheel_radius(i).max(0.05) * time.delta_secs();
                wheel.engine_force = 0.;
                wheel.brake = 0.;
            }
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn source_glide_snap_and_stall_horizon_prevent_unbounded_puppet_travel() {
        let mut puppet = Puppet::default();
        let mut pose = Transform::default();
        let mut row = Replica {
            position: [1., 0., 0.],
            lin_vel: [10., 0., 0.],
            ..default()
        };
        puppet.advance(&mut pose, &row, 0.02);
        assert!((pose.translation.x - (1. - (-0.24_f32).exp())).abs() < 1e-5);
        for _ in 0..200 {
            puppet.advance(&mut pose, &row, 0.02);
        }
        assert!((pose.translation.x - 6.).abs() < 0.001);
        assert_eq!(puppet.advance(&mut pose, &row, 0.02), 0.);
        row.position = [40., 0., 0.];
        puppet.advance(&mut pose, &row, 0.02);
        assert_eq!(pose.translation.x, 40.);
        assert_eq!(puppet.since_snapshot, 0.);
    }
    #[test]
    fn real_rapier_puppet_stays_fixed_until_local_driver_owns_the_single_body() {
        let mut app = super::super::tests::physics_app(None);
        let entity = super::super::tests::body(&mut app, "jeep", Vec3::Y * 10.);
        app.insert_resource(GameSnapshot(
            json!({"network":{"ownsWorld":false},"playerId":2}),
        ));
        for _ in 0..8 {
            app.update();
        }
        assert_eq!(
            *app.world().get::<RigidBody>(entity).unwrap(),
            RigidBody::KinematicPositionBased
        );
        assert!((app.world().get::<Transform>(entity).unwrap().translation.y - 10.).abs() < 0.001);
        app.world_mut()
            .get_mut::<VehicleBody>(entity)
            .unwrap()
            .driver_player_id = 2;
        for _ in 0..8 {
            app.update();
        }
        assert_eq!(
            *app.world().get::<RigidBody>(entity).unwrap(),
            RigidBody::Dynamic
        );
        assert!(
            app.world().get::<Transform>(entity).unwrap().translation.y < 9.99,
            "only the accepted driver's body simulates gravity"
        );
    }
}
