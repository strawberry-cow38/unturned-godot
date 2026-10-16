//! Original collision deceleration and rotating blade overlap rules. Native
//! contact facts go to the trusted host; only its accepted result changes health.
use super::*;
use crate::entities::ServiceManifest;

#[derive(Component)]
pub struct ContactState {
    previous: f32,
    peak: f32,
    grace: f32,
    cooldown: f32,
    rotors: [f32; 2],
}
impl ContactState {
    pub fn new(spec: &data::VehicleSpec) -> Self {
        Self {
            previous: 0.,
            peak: 0.,
            grace: if spec.plane && !spec.wheels().is_empty() {
                0.4
            } else {
                2.5
            },
            cooldown: 0.,
            rotors: [0.; 2],
        }
    }
    pub fn adopt_velocity(&mut self, spec: &data::VehicleSpec, velocity: Vec3) {
        let speed = if spec.heli || spec.plane {
            velocity.length()
        } else {
            velocity.xz().length()
        };
        self.previous = speed;
        self.peak = speed;
    }
    fn impact(
        &mut self,
        spec: &data::VehicleSpec,
        velocity: Vec3,
        parked: bool,
        dt: f32,
    ) -> Option<(f32, bool)> {
        let aircraft = spec.heli || spec.plane;
        let speed = if aircraft {
            velocity.length()
        } else {
            velocity.xz().length()
        };
        let decel = self.previous - speed;
        self.previous = speed;
        self.peak = speed.max(self.peak - dt * 6.);
        self.cooldown = (self.cooldown - dt).max(0.);
        self.grace = (self.grace - dt).max(0.);
        if parked
            || (aircraft && (self.grace > 0. || self.cooldown > 0.))
            || self.peak <= if aircraft { 5.5 } else { 5. }
            || decel <= 200. * dt
        {
            return None;
        }
        if aircraft {
            self.cooldown = 0.25;
            Some((decel * 18., self.peak >= 15.))
        } else {
            Some((self.peak * 20., false))
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FeedbackKind {
    Bonk,
    MainStrike,
    TailStrike,
}
#[derive(Message)]
pub struct Feedback {
    pub vehicle: Entity,
    pub kind: FeedbackKind,
    pub seed: u64,
}
#[derive(Message)]
pub struct DamageRequest {
    pub vehicle: Entity,
    pub net_id: u32,
    pub amount: f32,
    pub body_part: &'static str,
    pub explode_now: bool,
    pub position: Vec3,
    pub direction: Vec3,
    pub destructibles: Vec<(u32, f32)>,
    pub feedback: Option<FeedbackKind>,
}
fn disc_overlaps(
    context: &RapierContext,
    body: Entity,
    pose: &Transform,
    spec: &data::VehicleSpec,
    main: bool,
) -> Vec<Entity> {
    let (hub, radius, half_height, rotation) = if main {
        (spec.rotor_hub, spec.rotor_radius, 0.07, Quat::IDENTITY)
    } else {
        (
            spec.tail_rotor_hub,
            spec.tail_rotor_radius,
            0.05,
            Quat::from_rotation_z(std::f32::consts::FRAC_PI_2),
        )
    };
    if radius <= 0. {
        return vec![];
    }
    let mut hits = Vec::new();
    context.intersect_shape(
        pose.transform_point(Vec3::from_array(hub)),
        pose.rotation * rotation,
        &Collider::cylinder(half_height, radius),
        QueryFilter::default()
            .exclude_rigid_body(body)
            .exclude_collider(body)
            .groups(CollisionGroups::new(
                Group::ALL,
                Group::GROUP_1 | Group::GROUP_4,
            )),
        |entity, _| {
            if !hits.contains(&entity) {
                hits.push(entity);
            }
            true
        },
    );
    hits
}
pub fn detect(
    ui: Res<UiState>,
    snapshot: Option<Res<crate::player::GameSnapshot>>,
    context: ReadRapierContext,
    mut bodies: Query<(
        Entity,
        &mut VehicleBody,
        &mut ContactState,
        &Transform,
        &Velocity,
        &RigidBody,
    )>,
    objects: Query<&crate::world::WorldObject>,
    parents: Query<&ChildOf>,
    services: Res<ServiceManifest>,
    residency: Option<Res<crate::dedicated::Residency>>,
    mut hits: MessageWriter<DamageRequest>,
) {
    if snapshot
        .as_ref()
        .is_some_and(|s| !crate::network::owns_world(&s.0))
        || crate::player::simulation_paused(&ui)
    {
        return;
    }
    let context = context.single().ok();
    for (entity, mut body, mut state, pose, velocity, rigid) in &mut bodies {
        if body.flags & 32 != 0 {
            continue;
        }
        let spec = &data::definition(body.type_id).unwrap().spec;
        // A dedicated host advances many fixed steps per headless frame while a
        // remote driver's replica velocity changes once per frame, so that step
        // reads as a crash. The crash impact is judged only for bodies this host
        // simulates there (see DEDICATED_SERVER_PORT.md, Remaining).
        let sampled_replica = residency.is_some()
            && snapshot
                .as_ref()
                .is_some_and(|s| !coupling::state::simulates(&s.0, &body));
        if sampled_replica {
            state.adopt_velocity(spec, velocity.linear);
        }
        if let Some((amount, explode_now)) = (!sampled_replica)
            .then(|| state.impact(spec, velocity.linear, *rigid == RigidBody::Fixed, 0.02))
            .flatten()
        {
            hits.write(DamageRequest {
                vehicle: entity,
                net_id: body.net_id,
                amount,
                body_part: "hull",
                explode_now,
                position: pose.translation,
                direction: velocity.linear.normalize_or_zero(),
                destructibles: vec![],
                feedback: ((spec.heli || spec.plane) && !explode_now).then_some(FeedbackKind::Bonk),
            });
        }
        if !spec.heli {
            continue;
        }
        let Some(context) = context.as_ref() else {
            continue;
        };
        if (body.flight.main_health <= 0. || body.flight.tail_health <= 0.)
            && velocity.linear.length_squared() < 4.
            && context
                .cast_ray(
                    pose.translation,
                    Vec3::NEG_Y,
                    spec.ground_clearance() + 0.45,
                    true,
                    QueryFilter::default().exclude_rigid_body(entity),
                )
                .is_some()
        {
            body.flags &= !1;
            body.controls.engine = false;
        }
        for index in 0..2 {
            state.rotors[index] = (state.rotors[index] - 0.02).max(0.);
            if body.flight.spool <= 0.12 || state.rotors[index] > 0. {
                continue;
            }
            let touched = disc_overlaps(context, entity, pose, spec, index == 0);
            if touched.is_empty() {
                continue;
            }
            state.rotors[index] = 0.22;
            let mut destructibles = Vec::new();
            for mut entity in touched {
                for _ in 0..8 {
                    if let Ok(object) = objects.get(entity) {
                        if let Some(id) = services
                            .objects
                            .get(&object.source_index)
                            .and_then(|v| v["destructibleIndex"].as_u64())
                        {
                            if !destructibles.iter().any(|(known, _)| *known == id as u32) {
                                destructibles.push((id as u32, if index == 0 { 34. } else { 22. }));
                            }
                        }
                        break;
                    }
                    let Ok(parent) = parents.get(entity) else {
                        break;
                    };
                    entity = parent.parent();
                }
            }
            hits.write(DamageRequest {
                vehicle: entity,
                net_id: body.net_id,
                amount: if index == 0 { 7. } else { 6. },
                body_part: if index == 0 { "mainRotor" } else { "tailRotor" },
                explode_now: false,
                position: pose.transform_point(Vec3::from_array(if index == 0 {
                    spec.rotor_hub
                } else {
                    spec.tail_rotor_hub
                })),
                direction: Vec3::ZERO,
                destructibles,
                feedback: Some(if index == 0 {
                    FeedbackKind::MainStrike
                } else {
                    FeedbackKind::TailStrike
                }),
            });
        }
    }
}
pub fn publish(
    bridge: Res<CoreBridge>,
    snapshot: Res<crate::player::GameSnapshot>,
    mut hits: MessageReader<DamageRequest>,
    mut feedback: MessageWriter<Feedback>,
    mut serial: Local<u64>,
) {
    if snapshot.0.is_null()
        || snapshot.0["connected"] == false
        || !crate::network::owns_world(&snapshot.0)
    {
        hits.clear();
        return;
    }
    for hit in hits.read() {
        let props: Vec<_> = hit
            .destructibles
            .iter()
            .map(|(index, amount)| json!({"index":index,"amount":amount}))
            .collect();
        match bridge.request("worldVehicleDamage",json!({"netId":hit.net_id,"amount":hit.amount,"bodyPart":hit.body_part,"engineShare":if hit.body_part=="hull"{0.5}else{0.},
            "point":hit.position.to_array(),"direction":hit.direction.to_array(),"explodeNow":hit.explode_now,"destructibles":props})) {
            Ok(reply) if reply["result"]["accepted"]==true=>{if let Some(kind)=hit.feedback {*serial+=1;feedback.write(Feedback{vehicle:hit.vehicle,kind,seed:*serial});}},
            Ok(_)=>{},Err(e)=>warn!("Native vehicle contact damage: {e}"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn actual_rapier_aircraft_impact_generates_one_authoritative_explosion_request() {
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            RapierPhysicsPlugin::<NoUserData>::default(),
        ))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .init_resource::<ServiceManifest>()
        .add_message::<DamageRequest>()
        .add_systems(PostUpdate, detect.after(PhysicsSet::Writeback));
        app.world_mut().spawn((
            RigidBody::Fixed,
            Collider::cuboid(50., 0.25, 50.),
            Transform::from_xyz(0., -0.25, 0.),
        ));
        let vehicle = super::super::tests::body(&mut app, "huey", Vec3::new(0., 8., 0.));
        let mut contact = ContactState::new(&data::named("huey").unwrap().spec);
        contact.grace = 0.;
        app.world_mut().entity_mut(vehicle).insert((
            contact,
            Ccd::enabled(),
            Velocity {
                linear: Vec3::Y * -20.,
                angular: Vec3::ZERO,
            },
        ));
        let mut hits = Vec::new();
        for _ in 0..50 {
            app.update();
            hits.extend(
                app.world_mut()
                    .resource_mut::<Messages<DamageRequest>>()
                    .drain(),
            );
        }
        let explosions: Vec<_> = hits.iter().filter(|h| h.explode_now).collect();
        assert_eq!(
            explosions.len(),
            1,
            "actual native collision must cause one hard-impact fact, not a render-only blast"
        );
        assert_eq!(explosions[0].net_id, 1);
        assert_eq!(
            app.world().get::<VehicleBody>(vehicle).unwrap().health,
            data::named("huey").unwrap().spec.health * 10.,
            "contact detector must never mutate authoritative HP locally"
        );
    }
    #[test]
    fn real_rotor_overlap_ignores_own_hull_stopped_blades_and_removed_obstacle() {
        let mut app = App::new();
        app.add_plugins((
            MinimalPlugins,
            TransformPlugin,
            RapierPhysicsPlugin::<NoUserData>::default(),
        ))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(20),
        ))
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .init_resource::<ServiceManifest>()
        .add_message::<DamageRequest>()
        .add_systems(PostUpdate, detect.after(PhysicsSet::Writeback));
        let vehicle = super::super::tests::body(&mut app, "huey", Vec3::ZERO);
        let spec = &data::named("huey").unwrap().spec;
        app.world_mut()
            .entity_mut(vehicle)
            .insert((RigidBody::Fixed, ContactState::new(spec)));
        app.world_mut().spawn((
            Collider::ball(0.3),
            Transform::from_translation(Vec3::from_array(spec.rotor_hub)),
            ChildOf(vehicle),
        ));
        app.world_mut()
            .get_mut::<VehicleBody>(vehicle)
            .unwrap()
            .flight
            .spool = 0.4;
        for _ in 0..4 {
            app.update();
        }
        assert_eq!(
            app.world_mut()
                .resource_mut::<Messages<DamageRequest>>()
                .drain()
                .count(),
            0,
            "own rotor hub must never grind its own rotor"
        );
        let wall = app
            .world_mut()
            .spawn((
                RigidBody::Fixed,
                Collider::cuboid(0.1, 0.5, 0.1),
                Transform::from_translation(
                    Vec3::from_array(spec.rotor_hub) + Vec3::X * (spec.rotor_radius * 0.75),
                ),
            ))
            .id();
        app.update();
        let hits: Vec<_> = app
            .world_mut()
            .resource_mut::<Messages<DamageRequest>>()
            .drain()
            .collect();
        assert_eq!(hits.len(), 1);
        assert_eq!(hits[0].body_part, "mainRotor");
        assert_eq!(hits[0].amount, 7.);
        app.world_mut()
            .get_mut::<VehicleBody>(vehicle)
            .unwrap()
            .flight
            .spool = 0.;
        for _ in 0..20 {
            app.update();
        }
        assert_eq!(
            app.world_mut()
                .resource_mut::<Messages<DamageRequest>>()
                .drain()
                .count(),
            0,
            "stationary blades neither cut nor take damage"
        );
        app.world_mut().entity_mut(wall).despawn();
        app.world_mut()
            .get_mut::<VehicleBody>(vehicle)
            .unwrap()
            .flight
            .spool = 0.4;
        for _ in 0..14 {
            app.update();
        }
        assert_eq!(
            app.world_mut()
                .resource_mut::<Messages<DamageRequest>>()
                .drain()
                .count(),
            0,
            "destroyed prop no longer catches the live disc"
        );
    }
    #[test]
    fn slow_braking_spawn_drop_and_small_landing_do_not_damage_aircraft() {
        let spec = &data::named("huey").unwrap().spec;
        let mut s = ContactState::new(spec);
        assert!(s.impact(spec, Vec3::Y * -20., false, 0.02).is_none());
        assert!(
            s.impact(spec, Vec3::ZERO, false, 0.02).is_none(),
            "spawn grace must protect initial placement"
        );
        s.grace = 0.;
        s.peak = 0.;
        s.previous = 0.;
        assert!(s.impact(spec, Vec3::Y * -5., false, 0.02).is_none());
        assert!(
            s.impact(spec, Vec3::ZERO, false, 0.02).is_none(),
            "firm touchdown below5.5m/s is free"
        );
        for speed in (0..100).rev() {
            assert!(
                s.impact(spec, Vec3::Z * speed as f32 * 0.2, false, 0.02)
                    .is_none(),
                "normal braking is below200m/s²"
            );
        }
    }
    #[test]
    fn aircraft_full_vertical_impact_and_car_horizontal_impact_keep_distinct_source_rules() {
        let heli = &data::named("huey").unwrap().spec;
        let car = &data::named("jeep").unwrap().spec;
        let mut s = ContactState::new(heli);
        s.grace = 0.;
        s.impact(heli, Vec3::Y * -20., false, 0.02);
        assert!(
            s.impact(heli, Vec3::Y * -8., false, 0.02).unwrap().1,
            "high-speed vertical arrival bypasses the fuse"
        );
        assert!(
            s.impact(heli, Vec3::ZERO, false, 0.02).is_none(),
            "one bonk per cooldown"
        );
        let mut c = ContactState::new(car);
        c.impact(car, Vec3::Y * -20., false, 0.02);
        assert!(
            c.impact(car, Vec3::ZERO, false, 0.02).is_none(),
            "car spawn drop cannot look like a horizontal ram"
        );
        c.impact(car, Vec3::X * 20., false, 0.02);
        let (damage, instant) = c.impact(car, Vec3::X * 8., false, 0.02).unwrap();
        assert!((damage - 397.6).abs() < 0.01 && !instant);
        c.adopt_velocity(car, Vec3::ZERO);
        assert!(
            c.impact(car, Vec3::ZERO, false, 0.02).is_none(),
            "authority recovery does not fabricate a physical impact"
        );
    }
}
