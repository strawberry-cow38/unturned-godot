use super::*;
use crate::network::Target;

#[test]
fn dedicated_cli_owns_the_world_without_a_local_player_and_refuses_player_harnesses() {
    let options = Options::parse(
        ["--dedicated", "--port=47999", "--max-players=8", "--netlog"].map(str::to_owned),
    )
    .unwrap();
    assert!(options.play, "the shared session initializer must run");
    assert!(options.netlog);
    assert_eq!(
        options.network,
        Target::Dedicated {
            port: 47999,
            name: None,
            max_players: Some(8)
        }
    );
    assert!(options.network.networked() && options.network.owns_world());
    let mut request = json!({"mapId":"PEI"});
    options.network.configure(&mut request);
    // Unset name stays unset so the bridge applies the source UG_NAME default.
    assert_eq!(
        request,
        json!({"mapId":"PEI","dedicated":true,"listenPort":47999,"maxPlayers":8})
    );
    for args in [
        vec!["--dedicated", "--listen=47872"],
        vec!["--dedicated", "--connect=localhost"],
        vec!["--dedicated", "--headless"],
        vec!["--dedicated", "--map=Procedural"],
        vec!["--dedicated", "--new-island=/tmp/x"],
        vec!["--dedicated", "--shot=/tmp/x.png"],
        vec!["--dedicated", "--fixed-fps=20"],
        vec!["--dedicated", "--vehicle=jeep"],
        vec!["--dedicated", "--port=0"],
        vec!["--dedicated", "--max-players=33"],
    ] {
        assert!(
            Options::parse(args.iter().map(|a| a.to_string())).is_err(),
            "{args:?}"
        );
    }
    // Listen keeps its explicit defaults after --port/--server-name became optional.
    let listen = Options::parse(["--listen=47001".to_owned()]).unwrap();
    assert_eq!(
        listen.network,
        Target::Listen {
            port: 47001,
            name: "Bevy world".into(),
            max_players: 24
        }
    );
}

#[test]
fn dedicated_init_carries_the_hybrid_save_bans_and_map_root() {
    let options = Options::parse(
        ["--dedicated", "--unturned=/srv/Unturned", "--map=PEI"].map(str::to_owned),
    )
    .unwrap();
    let mut request = json!({});
    configure_init(&options, &mut request);
    assert!(request.get("savePath").is_none(), "the shared WorldSaveDriver path is set by the session");
    let bans = std::path::PathBuf::from(request["bansPath"].as_str().unwrap());
    assert!(bans.ends_with("bans.tsv"));
    assert!(
        bans.starts_with(std::env::temp_dir()) && crate::player::persistence::dedicated_save_path("PEI").starts_with(std::env::temp_dir()),
        "tests never touch the real ban list or saves: {bans:?}"
    );
    assert_eq!(request["mapRoot"], json!("/srv/Unturned/Maps/PEI"));
    let mut listen = json!({});
    configure_init(&Options::parse(["--listen=47002".to_owned()]).unwrap(), &mut listen);
    assert_eq!(listen, json!({}), "only the dedicated host gets save/ban paths");
}

#[test]
fn loaded_region_covers_every_authored_point_with_margin() {
    assert!(extent_of(&[]).is_none());
    let (center, radius) = extent_of(&[
        Vec2::new(-1360., -912.),
        Vec2::new(848., 888.),
        Vec2::new(0., 0.),
        Vec2::NAN,
    ])
    .unwrap();
    assert_eq!(center, Vec3::new(-256., 0., -12.));
    let corner = Vec2::new(848., 888.) - center.xz();
    assert!((radius - (corner.length() + EXTENT_MARGIN)).abs() < 0.01);
}

#[test]
fn unattended_vehicles_park_outside_every_players_listen_sized_region() {
    let mut residency = Residency::default();
    assert!(residency.outside(Vec3::ZERO), "no players: everything parks");
    residency.adopt(&json!({"players":[
        {"position":[0.,30.,0.]}, {"position":[1000.,30.,0.]}, {"position":null}]}));
    assert!(!residency.outside(Vec3::new(200., 0., 0.)));
    assert!(!residency.outside(Vec3::new(1000., 80., RESIDENCY_RADIUS - 1.)));
    assert!(residency.outside(Vec3::new(500., 0., 0.)), "between the two regions");
    assert!(residency.outside(Vec3::new(0., 0., -RESIDENCY_RADIUS - 1.)));
}

#[test]
fn shipped_pei_placements_and_spawns_fit_the_dedicated_region() {
    let mut config = WorldConfig::default();
    let Some(root) = config
        .map_root
        .clone()
        .filter(|r| r.join("Level.dat").is_file())
    else {
        config.map_root = None;
        let (center, radius) = authored_extent(&config).expect("shipped placements");
        assert!(radius > 1000. && center.length() < 1000.);
        return;
    };
    let (center, radius) = authored_extent(&config).unwrap();
    let spawns =
        crate::world::decode_player_spawns(&std::fs::read(root.join("Spawns/Players.dat")).unwrap())
            .unwrap();
    assert!(!spawns.is_empty());
    for (spawn, _) in spawns {
        assert!(spawn.xz().distance(center.xz()) + EXTENT_MARGIN <= radius + 0.01);
    }
}

/// A headless host has no audio output, so Bevy never retires a one-shot
/// sound; remote gunfire, footsteps and impacts would pile up over the uptime.
#[test]
fn one_shot_sounds_end_on_a_headless_host_and_loops_stay() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<AudioSource>()
        .add_systems(Last, reap_one_shot_audio);
    let looping = app
        .world_mut()
        .spawn((AudioPlayer::new(Handle::default()), PlaybackSettings::LOOP))
        .id();
    for _ in 0..200 {
        // A frame of remote gunfire and footsteps.
        for _ in 0..10 {
            app.world_mut()
                .spawn((AudioPlayer::new(Handle::default()), PlaybackSettings::DESPAWN));
        }
        let voice = app
            .world_mut()
            .spawn((AudioPlayer::new(Handle::default()), PlaybackSettings::REMOVE))
            .id();
        app.update();
        assert!(app.world().get::<AudioPlayer>(voice).is_none());
    }
    let sounds = app
        .world_mut()
        .query::<&AudioPlayer>()
        .iter(app.world())
        .count();
    assert_eq!(sounds, 1, "2,000 one-shots ended; the loop is kept");
    assert!(app.world().get::<AudioPlayer>(looping).is_some());
}
