use super::*;
use crate::{
    character::CharacterPlugin,
    content::ContentCache,
    player::GameSnapshot,
    rig::{RigPlayback, RigPlugin},
    ui::Screen,
};

mod visual;

fn app() -> App {
    let config = WorldConfig::default();
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    let reply = bridge
        .request(
            "init",
            json!({"contentRoot":config.content_root,"spawn":[0,0,-2]}),
        )
        .unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&reply["state"]);
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        RigPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<SkinnedMeshInverseBindposes>()
    .init_asset::<bevy::shader::Shader>()
    .init_resource::<ContentCache>()
    .insert_resource(config)
    .insert_resource(bridge)
    .insert_resource(model)
    .insert_resource(GameSnapshot(reply["state"].clone()))
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(ServiceManifest {
        map: "Fixture".into(),
        ..default()
    })
    .init_resource::<WorldSessionEpoch>()
    .insert_resource(PlayerInput::with_view(180., 0.))
    .init_resource::<WorldGeometry>()
    .init_resource::<Settings>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_resource::<InteractionFocus>()
    .add_message::<UiIntent>()
    .add_plugins((CharacterPlugin, NpcPlugin))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(20),
    ))
    .add_systems(
        Update,
        super::super::reset_focus.before(CollectInteractions),
    )
    .add_systems(
        Update,
        super::super::apply_interaction
            .in_set(ApplyInteractions)
            .after(CollectInteractions),
    );
    app.world_mut().spawn((
        PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"),
        Transform::from_xyz(0., 0., -2.),
    ));
    app.world_mut().spawn((
        PlayerCamera,
        Transform::from_xyz(0., 1.4, -2.).looking_at(Vec3::new(0., 1.4, 0.), Vec3::Y),
    ));
    app.update();
    app.update();
    app
}
fn character(app: &App, key: &str) -> Value {
    app.world().resource::<Registry>().catalog.as_ref().unwrap()["characters"]
        .as_array()
        .unwrap()
        .iter()
        .find(|c| c["key"] == key)
        .unwrap()
        .clone()
}
fn add(app: &mut App, character: Value, position: Vec3) {
    app.world_mut()
        .resource_mut::<ServiceManifest>()
        .npcs
        .push(json!({
        "key":character["key"],"character":character,"position":position.to_array(),"yaw":0.}));
}
fn root(app: &App, index: usize) -> Entity {
    app.world().resource::<Registry>().instances[&format!("map:{index}")].entity
}

fn authority_ray(app: &App, x: f32) -> Value {
    app.world()
        .resource::<CoreBridge>()
        .request(
            "worldRay",
            json!({
                "from":[x,1.,2.],"to":[x,1.,-2.]
            }),
        )
        .unwrap()["result"]
        .clone()
}

#[test]
fn host_capsule_follows_manifest_replacement_origin_and_session_without_stale_bodies() {
    let mut app = app();
    let chef = character(&app, "Chef");
    add(&mut app, chef, Vec3::ZERO);
    app.update();
    let first = authority_ray(&app, 0.);
    assert_eq!(first["hit"], true);
    assert!((crate::player::vector(&first["point"]).unwrap().z - 0.35).abs() < 0.0001);
    assert_eq!(first["destructibleIndex"], -1);
    assert_eq!(first["resourceIndex"], -1);
    app.world()
        .resource::<CoreBridge>()
        .request("worldGeometry", json!({"replace":true,"meshes":[]}))
        .unwrap();
    assert_eq!(authority_ray(&app, 0.)["hit"], false);
    app.world_mut().resource_mut::<WorldGeometry>().revision += 1;
    app.update();
    assert_eq!(authority_ray(&app, 0.)["hit"], true);
    app.world()
        .resource::<CoreBridge>()
        .request("rebase", json!({"shift":[256,0,0],"expectedEpoch":0}))
        .unwrap();
    app.world().resource::<CoreBridge>().adopt_origin(1);
    assert_eq!(
        authority_ray(&app, -256.)["hit"],
        true,
        "managed origin barrier moves the collider immediately"
    );
    shift_origin(app.world_mut(), Vec3::X * 256.);
    app.world_mut().resource_mut::<ServiceManifest>().npcs[0]["position"] = json!([-256., 0., 0.]);
    app.world_mut().resource_mut::<WorldGeometry>().revision += 1;
    app.update();
    assert_eq!(authority_ray(&app, 0.)["hit"], false);
    assert_eq!(authority_ray(&app, -256.)["hit"], true);
    app.world_mut()
        .resource_mut::<ServiceManifest>()
        .npcs
        .clear();
    app.update();
    assert_eq!(
        authority_ray(&app, -256.)["hit"],
        false,
        "removing the puppet removes the host body"
    );
    let content = app.world().resource::<WorldConfig>().content_root.clone();
    let reply = app
        .world()
        .resource::<CoreBridge>()
        .request("init", json!({"contentRoot":content}))
        .unwrap();
    app.world_mut().resource_mut::<GameSnapshot>().0 = reply["state"].clone();
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    let chef = character(&app, "Chef");
    add(&mut app, chef, Vec3::ZERO);
    app.update();
    assert_eq!(authority_ray(&app, -256.)["hit"], false);
    assert_eq!(authority_ray(&app, 0.)["hit"], true);
}

#[test]
fn remote_npc_puppets_keep_local_physics_without_publishing_host_capsules() {
    let mut app = app();
    app.world_mut().resource_mut::<GameSnapshot>().0["network"]["ownsWorld"] = json!(false);
    let chef = character(&app, "Chef");
    add(&mut app, chef, Vec3::ZERO);
    for _ in 0..5 {
        app.update();
    }
    assert!(app.world().get::<Collider>(root(&app, 0)).is_some());
    assert!(app.world().resource::<LookedAt>().0.is_some());
    assert_eq!(
        authority_ray(&app, 0.)["hit"],
        false,
        "a remote scene never writes authority geometry"
    );
}

#[test]
fn source_characters_keep_duplicate_numeric_ids_separate_and_measure_posed_chef_hat() {
    let mut app = app();
    let chef = character(&app, "Chef");
    let sous = character(&app, "Chef_Sous");
    assert_eq!(chef["id"], sous["id"]);
    assert_eq!(
        chef["displayName"], "Chef Leonard",
        "explicit DTO, not empty field-only object"
    );
    add(&mut app, chef, Vec3::ZERO);
    add(&mut app, sous, Vec3::X * 3.);
    for _ in 0..4 {
        app.update();
    }
    let entity = root(&app, 0);
    let other = root(&app, 1);
    assert_ne!(entity, other);
    let world = app.world_mut();
    let rig = world.get::<RigPlayback>(entity).unwrap();
    assert_eq!(rig.clip, "Idle_Stand");
    assert!(rig.time > 0.);
    assert!(rig.looping);
    let outfit = world.get::<Appearance>(entity).unwrap();
    assert_eq!(
        (outfit.shirt, outfit.pants, outfit.face),
        (230, 231, Some(20))
    );
    assert_eq!(outfit.gear, vec![239]);
    assert_eq!(outfit.skin, Color::Srgba(Srgba::hex("#4B3D31").unwrap()));
    let plate = world.get::<Npc>(entity).unwrap().plate;
    let y = world.get::<Transform>(plate).unwrap().translation.y;
    let skull = rig.bones["Skull"];
    let (gear, transform) = world
        .query::<(&GearItem, &GlobalTransform, &ChildOf)>()
        .iter(world)
        .find(|(g, _, p)| g.0 == 239 && p.parent() == skull)
        .map(|(g, t, _)| (g.0, *t))
        .unwrap();
    assert_eq!(gear, 239);
    assert!(
        transform.translation().y > 1.,
        "real posed skull, not bind-pose identity"
    );
    assert!(y > 2.05, "chef hat should raise nameplate: {y}");
    assert!(y < 3., "bad transform would float label far above hat: {y}");
    assert!(
        world
            .get::<Collider>(entity)
            .unwrap()
            .as_compound()
            .is_some()
    );
    assert_eq!(
        world.get::<CollisionGroups>(entity).unwrap().memberships,
        Group::GROUP_1
    );
    assert_eq!(*world.get::<RigidBody>(entity).unwrap(), RigidBody::Fixed);
}

#[test]
fn actual_physics_focus_is_occluded_and_f_waits_for_managed_accepted_dialogue() {
    let mut app = app();
    let chef = character(&app, "Chef");
    add(&mut app, chef, Vec3::ZERO);
    for _ in 0..5 {
        app.update();
    }
    let entity = root(&app, 0);
    let plate = app.world().get::<Npc>(entity).unwrap().plate;
    assert_eq!(app.world().resource::<LookedAt>().0, Some(entity));
    assert!(app.world().get::<NameplateEnabled>(plate).unwrap().0);
    assert_eq!(
        app.world()
            .resource::<InteractionFocus>()
            .candidate
            .as_ref()
            .unwrap()
            .action,
        "npcTalk"
    );
    assert_eq!(app.world().resource::<CurrentNpcSpeaker>().dialogue, 0);
    let wall = app
        .world_mut()
        .spawn((
            Transform::from_xyz(0., 1.3, -1.),
            Collider::cuboid(0.5, 1.3, 0.1),
            RigidBody::Fixed,
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    for _ in 0..3 {
        app.update();
    }
    assert!(app.world().resource::<LookedAt>().0.is_none());
    assert!(!app.world().get::<NameplateEnabled>(plate).unwrap().0);
    app.world_mut().despawn(wall);
    for _ in 0..3 {
        app.update();
    }
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.update();
    assert_eq!(app.world().resource::<CurrentNpcSpeaker>().key, "Chef");
    assert_eq!(
        app.world().resource::<UiState>().screen,
        Screen::Playing,
        "body focus doesn't optimistically open a conversation"
    );
    let bridge = app.world().resource::<CoreBridge>();
    for _ in 0..5 {
        bridge.request("tick", json!({})).unwrap();
    }
    let view = bridge
        .request("npcView", json!({"speakerKey":"Chef"}))
        .unwrap();
    assert_eq!(view["result"]["openDialogue"], 59);
    assert!(
        view["result"]["dialogue"]["pages"]
            .as_array()
            .is_some_and(|p| !p.is_empty())
    );
}

#[test]
fn npc_lifecycle_preserves_identity_across_unrelated_manifest_and_rebase_then_clears_on_session() {
    let mut app = app();
    let chef = character(&app, "Chef");
    add(&mut app, chef, Vec3::new(260., 4., 1.));
    app.update();
    let entity = root(&app, 0);
    app.world_mut().resource_mut::<ServiceManifest>().revision += 1;
    app.update();
    assert_eq!(root(&app, 0), entity);
    let shift = Vec3::X * 256.;
    shift_origin(app.world_mut(), shift);
    app.world_mut()
        .get_mut::<Transform>(entity)
        .unwrap()
        .translation -= shift;
    app.world_mut().resource_mut::<ServiceManifest>().npcs[0]["position"] = json!([4., 4., 1.]);
    app.update();
    assert_eq!(root(&app, 0), entity);
    assert_eq!(
        app.world().get::<Transform>(entity).unwrap().translation,
        Vec3::new(4., 4., 1.)
    );
    app.world_mut().resource_mut::<WorldSessionEpoch>().0 += 1;
    app.update();
    assert_ne!(root(&app, 0), entity);
    assert!(app.world().get_entity(entity).is_err());
    let current = root(&app, 0);
    app.world_mut().resource_mut::<UiModel>().connected = false;
    app.update();
    assert!(app.world().get_entity(current).is_err());
    assert!(app.world().resource::<Registry>().instances.is_empty());
}

#[test]
fn original_console_spawns_facing_player_and_routes_optional_talk_without_fabricating_map_npcs() {
    let mut app = app();
    assert!(app.world().resource::<Registry>().instances.is_empty());
    // Free camera gaze must not turn the console's original body look axis.
    let world = app.world_mut();
    world
        .query_filtered::<&mut Transform, With<PlayerCamera>>()
        .single_mut(world)
        .unwrap()
        .rotation = Quat::from_rotation_y(0.7);
    world
        .resource_mut::<WorldGeometry>()
        .terrain
        .push(crate::world::TerrainPatch {
            origin: Vec2::splat(-10.),
            cells: 1,
            spacing: 20.,
            heights: vec![0., 0., 10., 10.],
            layers: vec![],
            normals: vec![],
            holes: vec![],
            splat: None,
        });
    app.world_mut().write_message(UiIntent::Action {
        name: "console".into(),
        args: json!({"text":"npc Chef 3 talk"}),
    });
    app.update();
    app.update();
    let entity = app.world().resource::<Registry>().instances["console:0"].entity;
    let pose = app.world().get::<Transform>(entity).unwrap();
    assert!(
        pose.translation.distance(Vec3::new(0., 5.5, 1.)) < 0.001,
        "source ground sample ahead, not player's feet Y"
    );
    assert!(pose.forward().dot(Vec3::NEG_Z) > 0.999);
    assert!(app.world().resource::<ServiceManifest>().npcs.is_empty());
    assert_eq!(app.world().resource::<CurrentNpcSpeaker>().key, "Chef");
    assert_eq!(
        appearance(&json!({"skin":"bad","face":-42})).skin,
        Appearance::default().skin
    );
    assert_eq!(appearance(&json!({"face":900})).face, Some(31));
    assert_eq!(appearance(&json!({"shirt":0})).shirt, -1);
    // Unknown dialogue still shows the original NPC/name but sends no dialogue action.
    app.world_mut()
        .resource_mut::<Registry>()
        .catalog
        .as_mut()
        .unwrap()["dialogues"] = json!([]);
    app.world_mut().write_message(UiIntent::Action {
        name: "console".into(),
        args: json!({"text":"npc Chef 2 talk"}),
    });
    app.update();
    assert!(
        app.world()
            .resource::<UiModel>()
            .notices
            .iter()
            .any(|s| s.contains("has nothing to say"))
    );
}

#[test]
fn disabled_npc_plate_cannot_be_reopened_by_shared_distance_visibility() {
    // Shared range implementation has its own exact60m regression; this exercises
    // the production NPC gate with a real composed rig and UI opening/closing.
    let mut app = app();
    let chef = character(&app, "Chef");
    add(&mut app, chef, Vec3::ZERO);
    for _ in 0..4 {
        app.update();
    }
    let plate = app.world().get::<Npc>(root(&app, 0)).unwrap().plate;
    assert_eq!(
        *app.world().get::<Visibility>(plate).unwrap(),
        Visibility::Inherited
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Console;
    app.update();
    assert_eq!(
        *app.world().get::<Visibility>(plate).unwrap(),
        Visibility::Hidden
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.update();
    assert_eq!(
        *app.world().get::<Visibility>(plate).unwrap(),
        Visibility::Inherited
    );
    app.world_mut().insert_resource(crate::vehicle::Occupancy {
        seat: Some(crate::vehicle::OccupiedSeat {
            vehicle: plate,
            net_id: 1,
            seat: 0,
            body_pose: Transform::IDENTITY,
            eye_pose: Transform::IDENTITY,
            velocity: Vec3::ZERO,
            driver: true,
            aircraft: false,
        }),
    });
    app.update();
    assert_eq!(
        *app.world().get::<Visibility>(plate).unwrap(),
        Visibility::Hidden,
        "source riding/driving blocks NPC look focus"
    );
}
