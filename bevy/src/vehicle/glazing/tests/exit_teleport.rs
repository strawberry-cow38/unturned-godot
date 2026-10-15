use super::*;
use crate::player::{PlayerBody, PlayerInput};
use crate::vehicle::{Occupancy, OccupiedSeat};

#[test]
fn accepted_police_exit_teleports_the_capsule_without_sweeping_through_its_car() {
    let mut peaks = Vec::new();
    for legacy_sweep in [true, false] {
        let mut app = app();
        app.insert_resource(crate::world::WorldState {
            ready: true,
            ..default()
        })
        .insert_resource(TimestepMode::Fixed {
            dt: 0.02,
            substeps: 1,
        })
        .init_resource::<PlayerInput>()
        .init_resource::<Occupancy>()
        .init_resource::<crate::entities::ServiceManifest>()
        .insert_resource(crate::ui::UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .add_message::<super::super::super::contacts::DamageRequest>()
        .add_systems(Update, crate::player::sync_seated_body)
        .add_systems(
            PostUpdate,
            (
                super::super::super::contacts::detect,
                super::super::super::step_bodies,
            )
                .chain()
                .before(PhysicsSet::SyncBackend),
        );
        let slope = Quat::from_rotation_z(-13.35938_f32.to_radians());
        app.world_mut().spawn((
            RigidBody::Fixed,
            Collider::cuboid(30., 0.25, 30.),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            Transform::from_xyz(0., -0.82, 0.).with_rotation(slope),
        ));
        let car = build(&mut app, "police", Transform::from_rotation(slope));
        app.world_mut().entity_mut(car).insert((
            RigidBody::Dynamic,
            Ccd::enabled(),
            super::super::super::contacts::ContactState::new(
                &super::super::super::data::named("police").unwrap().spec,
            ),
        ));
        {
            let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
            b.flags = 0;
            b.driver_player_id = 0;
            b.controls = default();
        }
        let player = app
            .world_mut()
            .spawn((
                PlayerBody::for_motion_test(Vec3::ZERO, false, "stand"),
                RigidBody::KinematicPositionBased,
                KinematicCharacterController::default(),
                Collider::capsule(Vec3::Y * 0.35, Vec3::Y * 1.65, 0.35),
                CollisionGroups::new(Group::GROUP_2, Group::GROUP_1 | Group::GROUP_4),
                Transform::from_xyz(-5., 1., 0.),
            ))
            .id();
        for _ in 0..300 {
            app.update();
        }
        let def = super::super::super::data::named("police").unwrap();
        app.world_mut()
            .get_mut::<VehicleBody>(car)
            .unwrap()
            .driver_player_id = 1;
        for _ in 0..30 {
            let pose = *app.world().get::<Transform>(car).unwrap();
            let mut body_pose = pose;
            body_pose.translation = pose.transform_point(Vec3::from_array(def.seats[0]));
            app.world_mut().resource_mut::<Occupancy>().seat = Some(OccupiedSeat {
                vehicle: car,
                net_id: 1,
                seat: 0,
                body_pose,
                eye_pose: body_pose,
                velocity: Vec3::ZERO,
                driver: true,
                aircraft: false,
            });
            app.update();
        }
        assert!(app.world().get::<ColliderDisabled>(player).is_some());
        let pose = *app.world().get::<Transform>(car).unwrap();
        let doors = super::super::super::exit::doors(
            def,
            0,
            &pose,
            app.world().resource::<ContentCache>(),
            &WorldConfig::default().content_root,
        );
        let (clear, at) = {
            let mut state =
                SystemState::<(ReadRapierContext, Query<&ChildOf>)>::new(app.world_mut());
            let (read, parents) = state.get(app.world()).unwrap();
            super::super::super::exit::probe(
                &read.single().unwrap(),
                &parents,
                car,
                player,
                &pose,
                Vec3::from_array(def.seats[0]),
                &doors,
            )
        };
        assert!(
            clear,
            "source own-door probe must approve the standing spot"
        );
        app.world_mut().resource_mut::<Occupancy>().seat = None;
        app.world_mut().resource_mut::<GameSnapshot>().0 = json!({
            "connected":true, "player":{"position":at.to_array()}, "vehicles":[]
        });
        if legacy_sweep {
            // Negative control: old sync_seated_body only wrote Transform and
            // enabled the collider. Leave the same real physics body in place.
            app.world_mut().entity_mut(player).remove::<PlayerBody>();
            *app.world_mut().get_mut::<Transform>(player).unwrap() =
                Transform::from_translation(at);
            app.world_mut()
                .entity_mut(player)
                .remove::<ColliderDisabled>();
        }
        app.world_mut()
            .get_mut::<VehicleBody>(car)
            .unwrap()
            .driver_player_id = 0;
        app.world_mut()
            .resource_mut::<Messages<super::super::super::contacts::DamageRequest>>()
            .clear();
        let mut peak = 0_f32;
        let mut damage = 0_f32;
        for tick in 0..50 {
            app.update();
            let v = app.world().get::<Velocity>(car).unwrap().linear;
            peak = peak.max(v.xz().length());
            damage += app
                .world_mut()
                .resource_mut::<Messages<super::super::super::contacts::DamageRequest>>()
                .drain()
                .map(|h| h.amount)
                .sum::<f32>();
            if !legacy_sweep && tick == 0 {
                assert!(app.world().get::<ColliderDisabled>(player).is_none());
                assert_eq!(
                    app.world().get::<PlayerBody>(player).unwrap().stance(),
                    "stand"
                );
                let mut state = SystemState::<ReadRapierContext>::new(app.world_mut());
                let ctx = state.get(app.world()).unwrap();
                assert_eq!(
                    ctx.single()
                        .unwrap()
                        .cast_ray(
                            at + Vec3::X * 1. + Vec3::Y,
                            Vec3::NEG_X,
                            2.,
                            true,
                            QueryFilter::default()
                                .groups(CollisionGroups::new(Group::ALL, Group::GROUP_2))
                        )
                        .map(|h| h.0),
                    Some(player),
                    "the restored physical capsule must already be at the accepted doorway"
                );
            }
        }
        if legacy_sweep {
            assert!(
                peak > 10. && damage > 100.,
                "negative control must reproduce the recorded false crash: peak={peak}, damage={damage}"
            );
        } else {
            assert!(
                peak < 1.,
                "accepted exit cannot impart the teleport displacement as velocity: {peak}"
            );
            assert_eq!(
                damage, 0.,
                "normal source crash threshold must see no fabricated impact"
            );
        }
        peaks.push(peak);
    }
    eprintln!(
        "police slope exit peak m/s: legacy={}, corrected={}",
        peaks[0], peaks[1]
    );
}
