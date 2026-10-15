use super::*;
use bevy::ecs::system::RunSystemOnce;
use bevy_rapier3d::prelude::{Collider, RigidBody};
use serde_json::json;

fn definition() -> Definition {
    serde_json::from_value(json!({"rig":"rig.json","face":19,"tint":[0.82,0.66,0.52],
        "gunRotationDegrees":[0,0,90],"clip":null}))
    .unwrap()
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        crate::rig::RigPlugin,
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<SkinnedMeshInverseBindposes>()
    .init_asset::<bevy::shader::Shader>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldConfig>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<ItemCatalog>()
    .insert_resource(GameSnapshot(json!({"connected":true})))
    .add_plugins((crate::character::CharacterPlugin, CrewPlugin))
    .add_systems(Update, super::super::input::adopt.in_set(VehicleUpdate));
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = Some(definition());
    app
}
fn build(app: &mut App, key: &'static str, id: u32) -> Entity {
    let body = crate::vehicle::tests::body(app, key, Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = id;
    app.world_mut()
        .entity_mut(body)
        .insert(Visibility::Inherited);
    app.world_mut()
        .run_system_once(
            move |mut commands: Commands,
                  mut cache: ResMut<ContentCache>,
                  mut meshes: ResMut<Assets<Mesh>>,
                  mut materials: ResMut<Assets<StandardMaterial>>,
                  mut images: ResMut<Assets<Image>>,
                  assets: Res<AssetServer>| {
                crate::vehicle::render::build(
                    &mut commands,
                    body,
                    data::named(key).unwrap(),
                    0,
                    &WorldConfig::default().content_root,
                    &mut cache,
                    &mut meshes,
                    &mut materials,
                    &mut images,
                    &assets,
                );
            },
        )
        .unwrap();
    body
}
fn accepted(app: &mut App, id: u32, crew: [bool; 2], angles: [[f32; 2]; 2]) {
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"connected":true,
        "network":{"mode":"client","ownsWorld":false},
        "vehicleMounts":[{"vehicleNetId":id,"headOut":false,"mounts":crew.into_iter().enumerate().map(|(i,alive)|
            json!({"index":i,"yaw":angles[i][0],"pitch":angles[i][1],"ammo":400,
                "cooldownSeconds":0,"crewAlive":alive})).collect::<Vec<_>>()}]});
    app.update();
    app.update();
}
fn crew(app: &mut App) -> Vec<Entity> {
    app.world_mut()
        .query_filtered::<Entity, With<Crew>>()
        .iter(app.world())
        .collect()
}
#[test]
fn accepted_crew_materializes_real_bind_pose_and_hook_art_in_hull_frame() {
    let mut app = app();
    let body = build(&mut app, "huey", 71);
    app.update();
    assert!(
        crew(&mut app).is_empty(),
        "ordinary airframe has no invented crew"
    );
    let hull = Transform::from_xyz(4., 15., -7.).with_rotation(Quat::from_euler(
        EulerRot::YXZ,
        1.2,
        0.3,
        -0.2,
    ));
    *app.world_mut().get_mut::<Transform>(body).unwrap() = hull;
    accepted(&mut app, 71, [true, true], [[80., -20.], [-95., 15.]]);
    let people = crew(&mut app);
    assert_eq!(people.len(), 2);
    let defs = data::named("huey").unwrap().spec.turrets.as_ref().unwrap();
    for person in &people {
        let pose = app.world().get::<Transform>(*person).unwrap();
        assert!(
            defs.iter()
                .any(|d| pose.translation == Vec3::from_array(d.gunner_at))
        );
        assert_eq!(
            pose.rotation,
            Quat::IDENTITY,
            "source crew does not inherit mount yaw/pitch"
        );
        assert_eq!(app.world().get::<ChildOf>(*person).unwrap().parent(), body);
        let world = app.world().get::<GlobalTransform>(*person).unwrap();
        assert!((world.translation() - hull.transform_point(pose.translation)).length() < 0.0001);
        assert!(world.rotation().angle_between(hull.rotation) < 0.0001);
        let rig = app.world().get::<RigPlayback>(*person).unwrap();
        assert!(rig.clip.is_empty());
        assert!(rig.paused);
        assert_eq!(rig.bones.len(), 17);
        assert!(rig.overlay.is_none() && rig.additive.is_none());
        let appearance = app.world().get::<Appearance>(*person).unwrap();
        assert_eq!(appearance.face, Some(19));
        assert_eq!(appearance.skin, Color::srgb(0.82, 0.66, 0.52));
        let hook = rig.bones["Right_Hook"];
        let mut guns = app.world_mut().query_filtered::<(
            Entity,
            &ChildOf,
            &Transform,
            &Mesh3d,
            &MeshMaterial3d<StandardMaterial>,
        ), With<HeldGun>>();
        let (_, _, gun_pose, mesh, material) = guns
            .iter(app.world())
            .find(|(_, p, _, _, _)| p.parent() == hook)
            .unwrap();
        // Verify the actual source hook axes. angle_between uses acos(dot),
        // whose near-one rounding can report a nonzero angle for the equivalent
        // Euler-derived quaternion and from_rotation_z quaternion.
        assert!(
            (gun_pose.rotation * Vec3::Y + Vec3::X).length() < 0.0001,
            "source +90Z attachment: {:?}",
            gun_pose.rotation
        );
        assert!((gun_pose.rotation * Vec3::Z - Vec3::Z).length() < 0.0001);
        assert!(
            app.world()
                .resource::<Assets<Mesh>>()
                .get(&mesh.0)
                .unwrap()
                .count_vertices()
                > 100
        );
        assert!(
            app.world()
                .resource::<Assets<StandardMaterial>>()
                .get(&material.0)
                .unwrap()
                .cull_mode
                .is_none()
        );
        let descendants = app.world().get::<Children>(*person).unwrap();
        assert!(
            app.world().get::<Collider>(*person).is_none()
                && app.world().get::<RigidBody>(*person).is_none()
        );
        assert!(!descendants.is_empty());
    }
    let poses: Vec<_> = people
        .iter()
        .map(|e| *app.world().get::<GlobalTransform>(*e).unwrap())
        .collect();
    accepted(&mut app, 71, [true, true], [[-120., 30.], [100., -35.]]);
    for (entity, pose) in people.into_iter().zip(poses) {
        assert_eq!(
            *app.world().get::<GlobalTransform>(entity).unwrap(),
            pose,
            "gun traverse must not swivel crew"
        );
    }
}
#[test]
fn accepted_death_parent_removal_disconnect_and_session_retire_all_visual_children() {
    let mut app = app();
    let body = build(&mut app, "orca", 72);
    accepted(&mut app, 72, [true, true], [[0., 0.]; 2]);
    let before = crew(&mut app);
    assert_eq!(before.len(), 2);
    accepted(&mut app, 72, [false, true], [[0., 0.]; 2]);
    assert_eq!(crew(&mut app).len(), 1);
    let live = crew(&mut app)[0];
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(live).is_err());
    assert_eq!(crew(&mut app).len(), 1);
    app.world_mut().resource_mut::<GameSnapshot>().0["connected"] = false.into();
    app.update();
    assert!(crew(&mut app).is_empty());
    assert_eq!(
        app.world_mut()
            .query_filtered::<Entity, With<HeldGun>>()
            .iter(app.world())
            .count(),
        0
    );
    accepted(&mut app, 72, [true, true], [[0., 0.]; 2]);
    assert_eq!(crew(&mut app).len(), 2);
    app.world_mut().despawn(body);
    app.update();
    assert!(crew(&mut app).is_empty());
    assert!(app.world().resource::<Registry>().roots.is_empty());
}
#[test]
fn late_catalog_retries_and_missing_rig_uses_source_six_box_fallback() {
    let mut app = app();
    build(&mut app, "huey", 73);
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = None;
    accepted(&mut app, 73, [true, true], [[0., 0.]; 2]);
    assert!(crew(&mut app).is_empty());
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = Some(definition());
    app.update();
    assert_eq!(crew(&mut app).len(), 2);
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .npc_crew
        .as_mut()
        .unwrap()
        .rig = "absent-crew-rig.json".into();
    app.update();
    let people = crew(&mut app);
    assert_eq!(people.len(), 2);
    for person in people {
        assert!(app.world().get::<RigPlayback>(person).is_none());
        assert_eq!(app.world().get::<Children>(person).unwrap().len(), 6);
        assert!(app.world().get::<Collider>(person).is_none());
    }
}

#[test]
fn live_crew_remains_on_wreck_and_late_materials_receive_original_char_override() {
    let mut app = app();
    let body = build(&mut app, "huey", 74);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().flags |= 32;
    // Simulate a late joined wreck whose character catalog arrives afterward.
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = None;
    accepted(&mut app, 74, [true, true], [[0., 0.]; 2]);
    assert!(crew(&mut app).is_empty());
    app.world_mut().resource_mut::<ItemCatalog>().npc_crew = Some(definition());
    app.update();
    app.update();
    let people = crew(&mut app);
    assert_eq!(
        people.len(),
        2,
        "source explosion does not kill door gunners"
    );
    let mut checked = 0;
    for root in people {
        let mut descendants = vec![root];
        while let Some(entity) = descendants.pop() {
            if let Some(children) = app.world().get::<Children>(entity) {
                descendants.extend(children.iter());
            }
            if app.world().get::<Mesh3d>(entity).is_none() {
                continue;
            }
            assert!(
                app.world()
                    .get::<MeshMaterial3d<ClothesMaterial>>(entity)
                    .is_none()
            );
            let handle = &app
                .world()
                .get::<MeshMaterial3d<StandardMaterial>>(entity)
                .unwrap()
                .0;
            let material = app
                .world()
                .resource::<Assets<StandardMaterial>>()
                .get(handle)
                .unwrap();
            assert_eq!(material.base_color, Color::srgb(0.05, 0.05, 0.05));
            assert_eq!(material.cull_mode, None);
            checked += 1;
        }
    }
    assert!(checked >= 6, "both body, face and gun meshes are charred");
    accepted(&mut app, 74, [false, true], [[0., 0.]; 2]);
    assert_eq!(
        crew(&mut app).len(),
        1,
        "only accepted crew death hides one"
    );
}

mod visual;
