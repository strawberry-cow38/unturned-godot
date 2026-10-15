use super::*;
use bevy::mesh::VertexAttributeValues;
fn visuals() -> Visuals {
    serde_json::from_value(json!({"tracerTexture":"tracer.png","maxLength":10.,"headFraction":0.55,"segments":16,
        "muzzleAnchorDepth":0.6,"rocketMesh":"rocket_projectile.txt","rocketMeshRotation":[-90.,0.,0.],
        "rocketColor":[0.324,0.397,0.331],"rocketRoughness":0.75,"rocketSound":"projectile_fire.ogg",
        "rocketVolumeDb":2.,"rocketUnitSize":12.,"rocketMaxDistance":320.})).unwrap()
}
fn flight() -> Flight {
    serde_json::from_value(
        json!({"id":1,"position":[0.,0.,-5.],"origin":[0.,0.,0.],"velocity":[0.,0.,-940.],
    "silenced":false,"tracerWidth":0.032,"tracerColor":[1.9,0.85,0.2],"rocket":false}),
    )
    .unwrap()
}
fn positions(mesh: &Mesh) -> &Vec<[f32; 3]> {
    let VertexAttributeValues::Float32x3(v) = mesh.attribute(Mesh::ATTRIBUTE_POSITION).unwrap()
    else {
        panic!()
    };
    v
}
#[test]
fn original_crossed_strips_have_pointed_tail_round_nose_and_caliber_width() {
    let f = flight();
    let meta = visuals();
    let mesh = tracer(&f, &meta).unwrap();
    let p = positions(&mesh);
    assert_eq!(p.len(), 68);
    assert_eq!(mesh.indices().unwrap().len(), 192);
    assert_eq!(p[0], p[1]);
    assert_eq!(p[32], p[33]);
    assert_eq!(p[0], [0., 0., 5.]);
    assert_eq!(p[32], [0., 0., 0.]);
    // Before the head, width grows linearly; afterward it follows the round cap.
    let expected = 0.032 * (0.5 / 0.55);
    assert!((p[16][0].abs() - expected).abs() < 1e-6);
    let expected = 0.032 * (1. - ((0.75_f32 - 0.55) / 0.45).powi(2)).sqrt();
    assert!((p[24][0].abs() - expected).abs() < 1e-6);
    assert!((p[34 + 16][1].abs() - 0.032 * (0.5 / 0.55)).abs() < 1e-6);
}
#[test]
fn anchored_tail_caps_at_ten_metres_and_survives_vertical_and_origin_shift() {
    let mut f = flight();
    f.muzzle_anchor = Some([0.2, -0.1, -0.6]);
    let near = tracer(&f, &visuals()).unwrap();
    let tail = Vec3::from_array(positions(&near)[0]) + Vec3::from_array(f.position);
    assert!(tail.abs_diff_eq(Vec3::from_array(f.muzzle_anchor.unwrap()), 1e-5));
    f.position = [0., 0., -40.];
    let far = tracer(&f, &visuals()).unwrap();
    assert!((Vec3::from_array(positions(&far)[0]).length() - 10.).abs() < 1e-5);
    let before = positions(&far).clone();
    f.position[0] -= 1024.;
    f.origin[0] -= 1024.;
    f.muzzle_anchor.as_mut().unwrap()[0] -= 1024.;
    let shifted = tracer(&f, &visuals()).unwrap();
    for (a, b) in before.iter().zip(positions(&shifted)) {
        assert!(Vec3::from_array(*a).abs_diff_eq(Vec3::from_array(*b), 1e-4));
    }
    f.position = [0., 20., 0.];
    f.origin = [0.; 3];
    f.muzzle_anchor = None;
    assert!(
        positions(&tracer(&f, &visuals()).unwrap())
            .iter()
            .all(|p| Vec3::from_array(*p).is_finite())
    );
}
#[test]
fn suppressed_and_untravelled_rounds_have_no_streak() {
    let mut f = flight();
    f.silenced = true;
    assert!(tracer(&f, &visuals()).is_none());
    f.silenced = false;
    f.position = [0., 0., -0.099];
    assert!(tracer(&f, &visuals()).is_none());
    f.position = [0., 0., -0.1];
    assert!(tracer(&f, &visuals()).is_some());
}
#[test]
fn muzzle_projection_preserves_pixels_between_different_fovs_at_source_depth() {
    let view = Mat4::perspective_infinite_reverse_rh(60_f32.to_radians(), 16. / 9., 0.01);
    let world = Mat4::perspective_infinite_reverse_rh(90_f32.to_radians(), 16. / 9., 0.1);
    let muzzle = Vec3::new(0.2, -0.08, -0.7);
    let yaw = Quat::from_rotation_y(0.5);
    let offset = muzzle_projection(muzzle, Mat4::IDENTITY, view, world, yaw, 0.6).unwrap();
    let local = yaw.inverse() * offset;
    assert!((local.z + 0.6).abs() < 1e-6);
    assert!(
        world
            .project_point3(local)
            .truncate()
            .abs_diff_eq(view.project_point3(muzzle).truncate(), 1e-6)
    );
    assert!(
        !local.abs_diff_eq(muzzle, 0.01),
        "A world-space muzzle at the viewmodel FOV is not the screen-space anchor"
    );
    assert!(muzzle_projection(-muzzle, Mat4::IDENTITY, view, world, yaw, 0.6).is_none());
    let context = fire_context(0.5, Some(offset), Vec3::new(1025., 20., 4.));
    assert!(
        crate::player::vector(&context["muzzleAnchor"])
            .unwrap()
            .abs_diff_eq(Vec3::new(1025., 20., 4.) + offset, 1e-5)
    );
    assert!(
        fire_context(0., None, Vec3::ZERO)
            .get("muzzleAnchor")
            .is_none()
    );
}
#[test]
fn rocket_authored_positive_y_nose_follows_flight_with_separate_mesh_fix() {
    let flight = Vec3::new(0.4, 0.2, -0.8).normalize();
    let holder = orientation(flight).unwrap();
    let fix = Quat::from_rotation_x(visuals().rocket_mesh_rotation[0].to_radians());
    assert!((holder * fix * Vec3::Y).abs_diff_eq(flight, 1e-6));
    assert!(
        orientation(Vec3::Y).is_none(),
        "Source skips near-vertical LookAt and retains prior orientation"
    );
}
#[test]
fn live_bridge_catalog_and_cosmetic_snapshot_render_without_second_flight_simulation() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request("init", json!({"spawn":[0.,0.,0.],"starterKit":true,"contentRoot":WorldConfig::default().content_root}))
        .unwrap();
    let catalog = bridge.request("catalog", json!({})).unwrap();
    let meta: Visuals = serde_json::from_value(catalog["result"]["bulletVisuals"].clone()).unwrap();
    assert_eq!(meta.max_length, 10.);
    assert_eq!(meta.segments, 16);
    bridge
        .request(
            "action",
            json!({"name":"holdItem","args":{"page":0,"x":0,"y":0}}),
        )
        .unwrap();
    for _ in 0..8 {
        bridge.request("tick", json!({})).unwrap();
    }
    bridge.request("action",json!({"name":"fire","args":{"origin":[0.,1.75,0.],"dir":[0.,0.,-1.],"visual":{"aimAlpha":1.,"muzzleAnchor":[0.15,1.7,-0.6]}}})).unwrap();
    let mut last = None;
    for _ in 0..6 {
        let state = bridge.request("tick", json!({})).unwrap();
        if let Some(row) = state["state"]["bullets"].as_array().and_then(|a| a.first()) {
            last = Some(row.clone());
        }
    }
    let flight: Flight =
        serde_json::from_value(last.expect("accepted actual core bullet")).unwrap();
    assert!(tracer(&flight, &meta).is_some());
    assert!(flight.position[2] < -1.);
    assert_eq!(flight.muzzle_anchor, Some([0.15, 1.7, -0.6]));
}
fn scene() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, TransformPlugin, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .init_asset::<AudioSource>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldConfig>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<Settings>()
        .init_resource::<Flights>()
        .insert_resource(ItemCatalog {
            bullet_visuals: Some(visuals()),
            ..default()
        })
        .insert_resource(UiState {
            screen: crate::ui::Screen::Playing,
            ..default()
        })
        .insert_resource(GameSnapshot(json!({"bullets":[]})))
        .add_systems(Update, sync)
        .add_systems(
            PostUpdate,
            audio.after(bevy::transform::TransformSystems::Propagate),
        );
    app
}
#[test]
fn scene_diff_keeps_one_mesh_per_round_updates_head_and_retires_every_child_on_load_or_hit() {
    let mut app = scene();
    let row = json!({"id":17,"position":[0.,1.,-3.],"origin":[0.,1.,0.],"velocity":[0.,0.,-940.],
        "silenced":false,"tracerWidth":0.032,"tracerColor":[1.9,0.85,0.2],"rocket":false});
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"bullets":[row]});
    app.update();
    let root = app.world().resource::<Flights>().nodes[&17].entity;
    let child = app.world().resource::<Flights>().nodes[&17]
        .tracer
        .as_ref()
        .unwrap()
        .0;
    app.update();
    assert_eq!(app.world().resource::<Flights>().nodes.len(), 1);
    let material = app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(child)
        .unwrap();
    let tint = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(&material.0)
        .unwrap()
        .base_color;
    assert_eq!(tint.to_srgba(), Srgba::new(1.9, 0.85, 0.2, 1.));
    assert!(
        tint.to_linear().red > 4. && tint.to_linear().blue < 0.04,
        "Godot StandardMaterial albedo is source_color, including HDR components above1"
    );
    assert_eq!(
        app.world().resource::<Flights>().nodes[&17].entity,
        root,
        "Repeated snapshots retain the actual visual"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["bullets"][0]["position"] =
        json!([5., 1., -12.]);
    app.update();
    assert_eq!(
        app.world().get::<Transform>(root).unwrap().translation,
        Vec3::new(5., 1., -12.)
    );
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"bullets":[]});
    app.update();
    assert!(app.world().get_entity(root).is_err());
    assert!(app.world().get_entity(child).is_err());
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"bullets":[row]});
    app.update();
    let old = app.world().resource::<Flights>().nodes[&17].entity;
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert!(app.world().get_entity(old).is_err());
    assert_ne!(app.world().resource::<Flights>().nodes[&17].entity, old);
}
#[test]
fn rocket_uses_original_mesh_and_voice_lives_pauses_and_retires_with_accepted_round() {
    let mut app = scene();
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"bullets":[{
        "id":4,"position":[0.,2.,-5.],"origin":[0.,2.,0.],"velocity":[0.,0.,-80.],"silenced":true,
        "tracerWidth":0.032,"tracerColor":[1.9,0.85,0.2],"rocket":true}]});
    app.update();
    let node = &app.world().resource::<Flights>().nodes[&4];
    assert!(node.tracer.is_none());
    assert!(node.rocket.is_some());
    let world = app.world_mut();
    let voice = world
        .query_filtered::<Entity, With<RocketVoice>>()
        .single(world)
        .unwrap();
    assert!(!world.get::<PlaybackSettings>(voice).unwrap().paused);
    world.resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    app.update();
    assert!(app.world().get::<PlaybackSettings>(voice).unwrap().paused);
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Playing;
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"bullets":[]});
    app.update();
    assert!(
        app.world().get_entity(voice).is_err(),
        "Retired rounds cannot leave a permanent rocket roar"
    );
}
#[test]
fn launch_without_a_visible_length_has_valid_hidden_mesh_then_appears_on_travel() {
    let mut app = scene();
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"bullets":[{
        "id":9,"position":[0.,1.,0.],"origin":[0.,1.,0.],"velocity":[0.,0.,-940.],"silenced":false,
        "tracerWidth":0.032,"tracerColor":[1.9,0.85,0.2],"rocket":false}]});
    app.update();
    let (child, handle) = app.world().resource::<Flights>().nodes[&9]
        .tracer
        .as_ref()
        .unwrap()
        .clone();
    assert_eq!(
        app.world().get::<Visibility>(child),
        Some(&Visibility::Hidden)
    );
    assert!(
        app.world()
            .resource::<Assets<Mesh>>()
            .get(&handle)
            .unwrap()
            .contains_attribute(Mesh::ATTRIBUTE_POSITION)
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["bullets"][0]["position"] =
        json!([0., 1., -10.]);
    app.update();
    assert_eq!(
        app.world().get::<Visibility>(child),
        Some(&Visibility::Inherited)
    );
    assert_eq!(
        positions(app.world().resource::<Assets<Mesh>>().get(&handle).unwrap()).len(),
        68
    );
}
