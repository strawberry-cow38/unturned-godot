use super::*;
#[test]
fn source_explosion_falloff_and_damage_cap_are_distinct() {
    let camera = Transform::from_xyz(5., 1.75, 0.);
    let mut spring = Flinch::default();
    spring.explosion(
        &camera,
        ExplosionFlinch {
            point: Vec3::new(0., 1.75, 0.),
            radius: 10.,
            magnitude: 30.,
        },
    );
    assert!(
        spring
            .rotation
            .angle_between(Quat::from_rotation_z(-22.5_f32.to_radians()))
            < 1e-5
    );
    spring.rotation = Quat::IDENTITY;
    spring.damage(
        camera.rotation,
        Vec3::new(5., 0., 0.),
        Vec3::new(0., 200., 0.),
        100.,
    );
    assert!(
        spring
            .rotation
            .angle_between(Quat::from_rotation_z(-12.5_f32.to_radians()))
            < 1e-5
    );
}

#[test]
fn exact_radius_vertical_axis_and_nonfinite_input_never_poison_the_spring() {
    let camera = Transform::IDENTITY;
    let mut spring = Flinch::default();
    for (point, radius, magnitude) in [
        (Vec3::ZERO, 10., 30.),
        (Vec3::X * 10., 10., 30.),
        (Vec3::Y, 10., 30.),
        (Vec3::X, 0., 30.),
        (Vec3::X, 10., f32::NAN),
        (Vec3::NAN, 10., 30.),
    ] {
        spring.explosion(
            &camera,
            ExplosionFlinch {
                point,
                radius,
                magnitude,
            },
        );
        assert_eq!(spring.rotation, Quat::IDENTITY);
    }
    spring.rotation = Quat::from_xyzw(f32::NAN, 0., 0., 1.);
    spring.decay(0.02);
    assert_eq!(spring.rotation, Quat::IDENTITY);
}

#[test]
fn source_kicks_compose_in_order_then_slerp_four_per_second() {
    let mut spring = Flinch::default();
    spring.kick(Quat::IDENTITY, Vec3::X, 10.);
    spring.kick(Quat::IDENTITY, Vec3::Z, 20.);
    let combined =
        Quat::from_rotation_z(-10_f32.to_radians()) * Quat::from_rotation_x(20_f32.to_radians());
    assert!(spring.rotation.abs_diff_eq(combined, 1e-6));
    spring.decay(0.05);
    assert!(
        spring
            .rotation
            .abs_diff_eq(combined.slerp(Quat::IDENTITY, 0.2), 1e-6)
    );
    let base = super::super::lean::pivot(80., 0.2) * Quat::from_rotation_x(0.3);
    assert!(
        rotated(base, super::super::lean::pivot(80., 0.2), spring.rotation).abs_diff_eq(
            super::super::lean::pivot(80., 0.2) * spring.rotation * Quat::from_rotation_x(0.3),
            1e-6
        )
    );
}

#[derive(Component)]
struct Base(Transform);
fn reset_camera(mut cameras: Query<(&Base, &mut Transform), With<PlayerCamera>>) {
    for (base, mut transform) in &mut cameras {
        *transform = base.0;
    }
}
fn app() -> (App, Entity, Entity) {
    let mut app = App::new();
    let mut model = UiModel::default();
    model.connected = true;
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(model)
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<PlayerInput>()
    .init_resource::<Perspective>()
    .init_resource::<ItemCatalog>()
    .init_resource::<crate::vehicle::Occupancy>()
    .add_message::<CoreEvent>()
    .add_plugins(FlinchPlugin)
    .add_systems(Update, reset_camera.in_set(PlayerCameraUpdate));
    let body = app
        .world_mut()
        .spawn((
            PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
            Transform::IDENTITY,
        ))
        .id();
    let pose = Transform::from_xyz(0., 1.75, 0.);
    let camera = app.world_mut().spawn((PlayerCamera, pose, Base(pose))).id();
    app.update();
    app.update();
    (app, body, camera)
}
fn blast(app: &mut App) {
    app.world_mut().write_message(ExplosionFlinch {
        point: Vec3::new(-5., 1.75, 0.),
        radius: 10.,
        magnitude: 30.,
    });
}
#[test]
fn real_system_rebuilds_camera_without_accumulation_and_freezes_only_local_pause() {
    let (mut app, _, camera) = app();
    blast(&mut app);
    app.update();
    let first = app.world().resource::<Flinch>().rotation;
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .abs_diff_eq(first, 1e-6)
    );
    app.update();
    let second = first.slerp(Quat::IDENTITY, 0.08);
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .abs_diff_eq(second, 1e-6)
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    for _ in 0..5 {
        app.update();
    }
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .abs_diff_eq(second, 1e-6)
    );
    app.world_mut().resource_mut::<UiState>().networked = true;
    app.update();
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .abs_diff_eq(second.slerp(Quat::IDENTITY, 0.08), 1e-6)
    );
}
#[test]
fn accepted_hurt_and_blast_replay_guard_reset_on_session_death_and_respawn() {
    use serde_json::json;
    let (mut app, _, camera) = app();
    let event = CoreEvent {
        sequence: 5,
        kind: "hurt".into(),
        data: json!({"damage":100.,"hasSource":true,"sourcePos":{"x":-5.,"y":100.,"z":0.}}),
    };
    app.world_mut().write_message(event.clone());
    app.world_mut().write_message(event.clone());
    app.update();
    let expected = Quat::from_rotation_z((-12.5_f32 * 0.92).to_radians());
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .abs_diff_eq(expected, 1e-5)
    );
    app.world_mut().write_message(event);
    app.update();
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .abs_diff_eq(expected.slerp(Quat::IDENTITY, 0.08), 1e-5)
    );
    app.world_mut().resource_mut::<UiModel>().alive = false;
    app.update();
    assert_eq!(app.world().resource::<Flinch>().rotation, Quat::IDENTITY);
    app.world_mut().resource_mut::<UiModel>().alive = true;
    app.update();
    assert_eq!(
        app.world().get::<Transform>(camera).unwrap().rotation,
        Quat::IDENTITY
    );
    blast(&mut app);
    app.update();
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    blast(&mut app);
    app.update();
    assert_eq!(
        app.world().get::<Transform>(camera).unwrap().rotation,
        Quat::IDENTITY,
        "old-frame message cannot cross load boundary"
    );
    blast(&mut app);
    app.update();
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert_eq!(
        app.world().get::<Transform>(camera).unwrap().rotation,
        Quat::IDENTITY
    );
}
#[test]
fn accepted_effect_classification_preserves_source_radii_and_ignores_smoke() {
    use serde_json::json;
    let mut catalog = ItemCatalog::default();
    let smoke=serde_json::from_value(json!({"id":261,"name":"Smoke","type":"Throwable","asset":null,"mesh":null,"texture":null,"icon":null,"throwable":{"kind":"Smoke"}})).unwrap();
    catalog.items.insert(261, smoke);
    let event = |kind: &str, data| CoreEvent {
        sequence: 1,
        kind: kind.into(),
        data,
    };
    assert!(
        event_blast(
            &event(
                "grenadeExploded",
                json!({"itemId":261,"pos":[1,2,3],"radius":8})
            ),
            &catalog
        )
        .is_none()
    );
    let blast = event_blast(
        &event("bulletExplosion", json!({"position":[1,2,3],"radius":9})),
        &catalog,
    )
    .unwrap();
    assert_eq!((blast.radius, blast.magnitude), (18., 30.));
    let blast = event_blast(
        &event(
            "grenadeExploded",
            json!({"pos":{"x":1,"y":2,"z":3},"radius":3}),
        ),
        &catalog,
    )
    .unwrap();
    assert_eq!((blast.radius, blast.magnitude), (12., 30.));
    for (kind, radius, magnitude) in [
        ("vehicleExploded", 32., 45.),
        ("deployableExploded", 18., 28.),
    ] {
        let blast = event_blast(
            &event(kind, json!({"position":[1,2,3],"radius":99})),
            &catalog,
        )
        .unwrap();
        assert_eq!((blast.radius, blast.magnitude), (radius, magnitude));
    }
}
#[test]
fn third_person_flinch_moves_shoulder_sweep_and_respects_actual_world_collision() {
    let (mut app, _, camera) = app();
    app.world_mut().resource_mut::<Perspective>().first_person = false;
    app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(5., 5., 0.1),
        Transform::from_xyz(0., 1.6, 1.3),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
    ));
    app.update();
    app.update();
    app.world_mut().resource_mut::<Flinch>().rotation = Quat::from_rotation_z(0.4);
    app.update();
    let pose = *app.world().get::<Transform>(camera).unwrap();
    let origin = Vec3::Y * app.world().resource::<Perspective>().pivot_height;
    let delta = pose.translation - origin;
    assert!(
        delta.length() > 0.1 && delta.length() < 1.5,
        "real .39m camera capsule must stop before wall: {delta}"
    );
    assert!(
        delta
            .normalize()
            .abs_diff_eq(pose.rotation * camera::offset(1.), 1e-5)
    );
    let mut blocked = false;
    let mut system = bevy::ecs::system::SystemState::<ReadRapierContext>::new(app.world_mut());
    system
        .get(app.world())
        .unwrap()
        .single()
        .unwrap()
        .intersect_shape(
            pose.translation,
            Quat::IDENTITY,
            &Collider::ball(0.39),
            QueryFilter::default(),
            |_, _| {
                blocked = true;
                false
            },
        );
    assert!(
        !blocked,
        "flinch must not rotate a third-person camera through its wall"
    );
}
#[test]
fn mounted_message_kicks_seated_camera_instead_of_being_overwritten() {
    let (mut app, body, camera) = app();
    let seat = Transform::from_xyz(0., 1.75, 0.).with_rotation(Quat::from_rotation_y(0.7));
    app.world_mut().get_mut::<Base>(camera).unwrap().0 = seat;
    app.world_mut()
        .resource_mut::<crate::vehicle::Occupancy>()
        .seat = Some(crate::vehicle::OccupiedSeat {
        vehicle: body,
        net_id: 1,
        seat: 1,
        body_pose: Transform::IDENTITY,
        eye_pose: seat,
        velocity: Vec3::ZERO,
        driver: false,
        aircraft: false,
    });
    app.world_mut().write_message(ExplosionFlinch {
        point: Vec3::new(-5., 1.75, 0.),
        radius: 40.,
        magnitude: 4.,
    });
    app.update();
    let pose = app.world().get::<Transform>(camera).unwrap();
    assert_eq!(pose.translation, seat.translation);
    assert!(pose.rotation.angle_between(seat.rotation) > 3_f32.to_radians());
    assert!(pose.rotation.abs_diff_eq(
        seat.rotation * app.world().resource::<Flinch>().rotation,
        1e-6
    ));
    app.world_mut().resource_mut::<Perspective>().first_person = false;
    app.update();
    assert!(
        app.world()
            .get::<Transform>(camera)
            .unwrap()
            .rotation
            .angle_between(seat.rotation)
            > 0.01
    );
}

#[test]
fn third_person_uses_existing_accepted_gun_impulse_once_while_first_person_keeps_only_aim_recoil() {
    use serde_json::json;
    fn recoil(
        mut recoil: ResMut<crate::recoil::Recoil>,
        snapshot: Res<super::super::GameSnapshot>,
        catalog: Res<ItemCatalog>,
        time: Res<Time>,
    ) {
        recoil.update(&snapshot.0, &catalog, 0, time.delta_secs(), false);
    }
    for first_person in [true, false] {
        let (mut app, _, camera) = app();
        app.world_mut().resource_mut::<Perspective>().first_person = first_person;
        app.init_resource::<crate::recoil::Recoil>()
            .insert_resource(super::super::GameSnapshot(
                json!({"playerId":1,"events":[],"player":{"stance":"stand"}}),
            ))
            .add_systems(Update, recoil.before(FlinchUpdate));
        app.world_mut().resource_mut::<ItemCatalog>().items.insert(4,serde_json::from_value(json!({
            "id":4,"name":"test","type":"Gun","asset":"eaglefire","gun":{"recoilMinY":4.,"recoilMaxY":4.}
        })).unwrap());
        app.update();
        app.world_mut()
            .resource_mut::<super::super::GameSnapshot>()
            .0["events"] = json!([{"seq":1,"kind":"fire","data":{"playerId":1,"gun":"eaglefire","recoilMultiplier":1.}}]);
        app.update();
        let expected = Quat::from_rotation_x(if first_person {
            0.
        } else {
            (4_f32 * 0.35 * 0.92).to_radians()
        });
        assert!(
            app.world()
                .get::<Transform>(camera)
                .unwrap()
                .rotation
                .abs_diff_eq(expected, 1e-6)
        );
        app.update();
        assert!(
            app.world()
                .get::<Transform>(camera)
                .unwrap()
                .rotation
                .abs_diff_eq(expected.slerp(Quat::IDENTITY, 0.08), 1e-6)
        );
    }
}

#[test]
fn accepted_event_camera_kick_precedes_real_interaction_ray_without_changing_aim() {
    #[derive(Resource, Default)]
    struct Emit(bool);
    #[derive(Resource, Default)]
    struct Seen {
        direction: Vec3,
        hit: Option<Entity>,
    }
    fn publish(mut emit: ResMut<Emit>, mut events: MessageWriter<CoreEvent>) {
        if std::mem::take(&mut emit.0) {
            events.write(CoreEvent {
                sequence: 1,
                kind: "grenadeExploded".into(),
                data: serde_json::json!({"pos":[0,1.75,-5],"radius":5}),
            });
        }
    }
    fn look(
        context: ReadRapierContext,
        cameras: Query<&Transform, With<PlayerCamera>>,
        mut seen: ResMut<Seen>,
    ) {
        let camera = cameras.single().unwrap();
        seen.direction = *camera.forward();
        seen.hit = context
            .single()
            .unwrap()
            .cast_ray(
                camera.translation,
                seen.direction,
                6.,
                true,
                QueryFilter::default(),
            )
            .map(|h| h.0);
    }
    let (mut app, _, camera) = app();
    app.configure_sets(
        Update,
        crate::entities::ApplyInteractions.after(crate::entities::CollectInteractions),
    )
    .init_resource::<Emit>()
    .init_resource::<Seen>()
    .add_systems(Update, publish.in_set(crate::entities::CoreEventsReady))
    .add_systems(Update, look.in_set(crate::entities::CollectInteractions));
    let mut expected = Flinch::default();
    expected.explosion(
        &Transform::from_xyz(0., 1.75, 0.),
        ExplosionFlinch {
            point: Vec3::new(0., 1.75, -5.),
            radius: 12.,
            magnitude: 30.,
        },
    );
    expected.decay(0.02);
    let direction = expected.rotation * Vec3::NEG_Z;
    let target = app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::ball(0.08),
            Transform::from_translation(Vec3::new(0., 1.75, 0.) + direction * 3.),
        ))
        .id();
    app.update();
    app.update();
    assert_eq!(
        app.world().resource::<Seen>().hit,
        None,
        "baseline ray must miss the offset target"
    );
    app.world_mut().resource_mut::<Emit>().0 = true;
    app.update();
    let seen = app.world().resource::<Seen>();
    assert_eq!(
        seen.hit,
        Some(target),
        "same-frame interaction ray must follow the visible kick"
    );
    assert!(seen.direction.abs_diff_eq(direction, 1e-6));
    assert!(seen.direction.abs_diff_eq(
        *app.world().get::<Transform>(camera).unwrap().forward(),
        1e-6
    ));
    assert_eq!(app.world().resource::<PlayerInput>().world_yaw(), 0.);
    assert_eq!(app.world().resource::<PlayerInput>().pitch(), 0.);
}

mod visual;
