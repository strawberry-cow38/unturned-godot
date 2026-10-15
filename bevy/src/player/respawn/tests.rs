use super::*;

#[test]
fn respawn_angle_is_consumed_once_independently_of_position_corrections_and_session_reset() {
    let mut session = Session::default();
    let mut input = PlayerInput {
        yaw: 23.,
        pitch: 41.,
        ..default()
    };
    let fact = json!({"player":{"respawnLook":{"version":2,"yaw":137.25,"pitch":0},"correctionVersion":7}});
    adopt(&mut input, &mut session, &fact);
    assert_eq!((input.yaw, input.pitch), (137.25, 0.));
    input.yaw = 78.;
    input.pitch = -19.;
    let mut late = fact.clone();
    late["player"]["correctionVersion"] = json!(8);
    late["player"]["yaw"] = json!(11);
    late["player"]["pitch"] = json!(42);
    adopt(&mut input, &mut session, &late);
    assert_eq!(
        (input.yaw, input.pitch),
        (78., -19.),
        "late recovery or snapshot cannot replay the old facing"
    );
    let bed = json!({"player":{"respawnLook":{"version":3,"yaw":null,"pitch":0}}});
    adopt(&mut input, &mut session, &bed);
    assert_eq!(
        (input.yaw, input.pitch),
        (78., 0.),
        "source bed yaw is deliberately ignored"
    );
    input.pitch = 13.;
    adopt(
        &mut input,
        &mut session,
        &json!({"player":{"respawnLook":{"version":0}}}),
    );
    assert_eq!((input.yaw, input.pitch), (78., 13.));
    adopt(
        &mut input,
        &mut session,
        &json!({"player":{"respawnLook":{"version":1,"yaw":null}}}),
    );
    assert_eq!(
        (input.yaw, input.pitch),
        (78., 0.),
        "new session/default map resets pitch without leaking old finite yaw"
    );
}

#[test]
fn actual_bridge_respawn_fact_resets_pitch_and_does_not_reapply_after_ordinary_tick() {
    let project = std::path::Path::new(env!("CARGO_MANIFEST_DIR"));
    let bridge = CoreBridge::launch(project).unwrap();
    let initial = bridge
        .request(
            "init",
            json!({"contentRoot":project.join("../game/content"),"spawn":[20,40,20],"yaw":63}),
        )
        .unwrap();
    let mut model = UiModel::default();
    model.adopt_snapshot(&initial["state"]);
    assert!(model.connected);
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .insert_resource(bridge)
        .insert_resource(model)
        .insert_resource(UiState {
            screen: Screen::Playing,
            ..default()
        })
        .insert_resource(Session {
            initialized: true,
            ..default()
        })
        .insert_resource(PlayerInput {
            yaw: 63.,
            pitch: 41.,
            ..default()
        })
        .init_resource::<GameSnapshot>()
        .add_systems(Update, publish_body);
    app.world_mut().spawn((
        Transform::from_translation(Vec3::new(20., 40., 20.)),
        PlayerBody {
            velocity: Vec3::ZERO,
            grounded: false,
            height: 2.,
            stance: "stand".into(),
            eye_height: 1.75,
            locomotion: default(),
        },
    ));
    app.world()
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"console","args":{"text":"sethp 0"}}),
        )
        .unwrap();
    for _ in 0..5 {
        app.update();
    }
    assert!(!app.world().resource::<UiModel>().alive);
    app.world()
        .resource::<CoreBridge>()
        .request("action", json!({"name":"respawn","args":{}}))
        .unwrap();
    for _ in 0..10 {
        app.update();
    }
    assert!(app.world().resource::<UiModel>().alive);
    let input = app.world().resource::<PlayerInput>();
    assert_eq!(
        (input.yaw, input.pitch),
        (63., 0.),
        "actual publisher must adopt the accepted generic pitch reset"
    );
    {
        let mut input = app.world_mut().resource_mut::<PlayerInput>();
        input.yaw = 97.;
        input.pitch = -12.;
    }
    // A later real position recovery must preserve the player's new look.
    app.world()
        .resource::<CoreBridge>()
        .request(
            "action",
            json!({"name":"console","args":{"text":"teleport 22 40 20"}}),
        )
        .unwrap();
    for _ in 0..10 {
        app.update();
    }
    let input = app.world().resource::<PlayerInput>();
    assert_eq!((input.yaw, input.pitch), (97., -12.));
    assert!(app.world().resource::<Session>().correction_version > 0);
}
