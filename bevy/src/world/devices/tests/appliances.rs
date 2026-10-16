//! Checkpoint 2: vending machines, radios and patient monitors.
use super::*;
use crate::{bridge::CoreBridge, ui::UiModel};
use vending::{State, color_of, state_for};

#[test]
fn vending_states_rank_as_the_source_does() {
    // Mid-vend first, then power (a dollar would not fix it), then the seam, then the wallet.
    assert_eq!(state_for(0.3, false, false, 0), State::Dispensing);
    assert_eq!(state_for(0., false, false, 9), State::NoPower);
    assert_eq!(state_for(0., true, false, 9), State::OutOfOrder);
    assert_eq!(state_for(0., true, true, 0), State::NoFunds);
    assert_eq!(state_for(0., true, true, 1), State::Ready);
    assert_eq!(color_of(State::OutOfOrder), color_of(State::NoPower));
    assert!(vending::is_vendor_prop("Vendor_0") && vending::is_vendor_prop("Vendor_1"));
    assert!(!vending::is_vendor_prop("Vendor_2"));
}

/// Godot oracle (`bevy/tools/VendingOracle.cs` on `--proptest=Vendor_0` at
/// RotationDegrees(270, 0, 0)): PromptAnchor (0, 2.8, -0.5723389) and the
/// private DispensePoint (0, 0.30000004, -1.494679).
#[test]
fn the_tray_and_prompt_anchor_match_the_godot_vending_machine() {
    let data = obj("Vendor_0");
    let rest = Transform::from_rotation(Quat::from_rotation_x(270f32.to_radians()));
    let v = vending::Vendor::for_test(vending::COLA, data.min, data.max, rest);
    assert!(v.dispense_point().distance(Vec3::new(0., 0.300_000_04, -1.494_679)) < 1e-4, "{}", v.dispense_point());
    assert!(v.prompt_anchor(&rest).distance(Vec3::new(0., 2.8, -0.572_338_9)) < 1e-4, "{}", v.prompt_anchor(&rest));
    assert_eq!(v.prompt("F"), "[F] $1 cola");
}

#[test]
fn the_radio_board_reads_the_days_outlook_not_the_sky() {
    let weather = json!({"weather":{"name":"Rain","today":{"wet":true,"name":"Storm"},"tomorrow":{"wet":false,"name":"Rain"}}});
    assert_eq!(radio::forecast(&weather).unwrap(), "TODAY  Storm\nTOMORROW  Clear");
    assert!(radio::forecast(&json!({})).is_none(), "no WeatherManager: a blank board");
}

#[test]
fn the_monitor_overlay_sits_four_millimetres_proud_of_the_modelled_trace() {
    let mesh = monitor::screen_mesh();
    let Some(bevy::mesh::VertexAttributeValues::Float32x3(p)) = mesh.attribute(Mesh::ATTRIBUTE_POSITION) else {
        panic!()
    };
    assert!(p.iter().all(|v| (v[1] - 0.2221).abs() < 1e-6));
    let near = |a: [f32; 3], b: [f32; 3]| Vec3::from_array(a).distance(Vec3::from_array(b)) < 1e-6;
    assert!(near(p[0], [monitor::SCREEN_X0, 0.2221, monitor::SCREEN_Z0]));
    assert!(near(p[2], [monitor::SCREEN_X1, 0.2221, monitor::SCREEN_Z1]));
    let Some(bevy::mesh::VertexAttributeValues::Float32x2(uv)) = mesh.attribute(Mesh::ATTRIBUTE_UV_0) else {
        panic!()
    };
    assert_eq!(uv[0], [1., 0.], "UV (1 - u, v)");
    assert_eq!(uv[2], [0., 1.]);
}

/// One retail prop in front of the camera, through the production systems.
fn prop_app(name: &str, index: usize, euler: Vec3, snapshot: Value, model: UiModel) -> (App, Entity) {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        bevy_rapier3d::prelude::RapierPhysicsPlugin::<bevy_rapier3d::prelude::NoUserData>::default()
            .in_fixed_schedule(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .init_asset::<monitor::EcgMaterial>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(Duration::from_millis(20)))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .insert_resource(GameSnapshot(snapshot))
    .insert_resource(model)
    .insert_resource(catalog())
    .insert_resource(WorldConfig::default())
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<InteractionFocus>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_resource::<crate::info_billboard::FocusedInfo>()
    .init_resource::<DeviceState>()
    .init_resource::<Mains>()
    .init_resource::<monitor::Clock>()
    .configure_sets(Update, crate::entities::ApplyInteractions.after(CollectInteractions))
    .add_systems(
        Update,
        (
            reset_epoch,
            mains,
            (vending::attach, radio::attach, monitor::attach),
            ((vending::shake, vending::tint).chain(), radio::hiss, monitor::tick),
            |mut f: ResMut<InteractionFocus>, mut i: ResMut<crate::info_billboard::FocusedInfo>| {
                *f = default();
                i.0 = None;
            },
        )
            .chain()
            .before(CollectInteractions),
    )
    .add_systems(Update, collect_focus.in_set(CollectInteractions))
    .add_systems(
        Update,
        (vending::press, vending::prompt, radio::press, monitor::press)
            .chain()
            .after(CollectInteractions)
            .before(crate::entities::ApplyInteractions),
    );
    let data = obj(name);
    let world = app.world_mut();
    let material = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial::default());
    let xf = crate::world::placement_transform(Vec3::new(0., 0., -2.), euler, Vec3::ONE);
    let mesh = world.resource_mut::<Assets<Mesh>>().add(data.mesh());
    let half = (data.max - data.min) * 0.5;
    let prop = world
        .spawn((
            Mesh3d(mesh),
            MeshMaterial3d(material),
            xf,
            WorldObject { guid: String::new(), asset_name: name.into(), source_index: index },
            PlacementEuler(euler),
            bevy_rapier3d::prelude::RigidBody::Fixed,
        ))
        .id();
    world.spawn((
        bevy_rapier3d::prelude::Collider::cuboid(half.x, half.y, half.z),
        Transform::from_translation((data.min + data.max) * 0.5),
        ChildOf(prop),
    ));
    let target = xf.transform_point((data.min + data.max) * 0.5);
    world.spawn((PlayerCamera, Transform::from_translation(target + Vec3::new(0., 0.2, 1.8)).looking_at(target, Vec3::Y)));
    (app, prop)
}
fn grid(on: bool) -> Value {
    json!({"player":{"alive":true},"deployables":[{"netId":1,"defId":9200,"toggledOn":on}],
        "weather":{"today":{"wet":false,"name":"Clear"},"tomorrow":{"wet":true,"name":"Rain"}}})
}

#[test]
fn a_radio_is_off_by_default_and_plays_only_while_switched_on_and_fed() {
    let (mut app, prop) = prop_app("Radio_0", 31, Vec3::new(270., 15., 0.), grid(true), UiModel::default());
    step(&mut app, 4);
    let playing = |app: &App| app.world().get::<radio::Radio>(prop).unwrap().playing(app.world().resource::<Mains>().live);
    assert!(!playing(&app), "RadioDevice defaults to OFF");
    {
        let focus = app.world().resource::<InteractionFocus>();
        assert!(focus.selected_args("toggleRadio", &json!({"sourceIndex":31})));
        assert_eq!(focus.outlined(), Some((prop, Color::WHITE)));
    }
    press_f(&mut app);
    assert!(playing(&app));
    assert!(app.world().resource::<DeviceState>().radio_on.contains(&31));
    app.world_mut().resource_mut::<GameSnapshot>().0 = grid(false);
    step(&mut app, 15);
    assert!(app.world().get::<radio::Radio>(prop).unwrap().on, "the switch stays on");
    assert!(!playing(&app), "a mains-fed set dies with the grid");
    press_f(&mut app);
    assert!(!app.world().get::<radio::Radio>(prop).unwrap().on);
}

#[test]
fn a_monitor_shows_its_ecg_while_on_and_fed_and_the_off_panel_otherwise() {
    let (mut app, prop) = prop_app(monitor::PROP, 44, Vec3::new(270., 0., 0.), grid(true), UiModel::default());
    step(&mut app, 4);
    let screen = app.world().get::<monitor::Monitor>(prop).unwrap().screen;
    let ecg = |app: &App| app.world().get::<MeshMaterial3d<monitor::EcgMaterial>>(screen).is_some();
    assert!(ecg(&app), "HeartMonitor starts ON");
    {
        let focus = app.world().resource::<InteractionFocus>();
        assert!(focus.selected_args("toggleMonitor", &json!({"sourceIndex":44})));
        assert_eq!(focus.outlined(), Some((prop, Color::WHITE)));
    }
    let monitor = app.world().get::<monitor::Monitor>(prop).unwrap();
    let handle = app.world().get::<MeshMaterial3d<monitor::EcgMaterial>>(screen).unwrap().0.clone();
    let params = app.world().resource::<Assets<monitor::EcgMaterial>>().get(&handle).unwrap().params;
    assert_eq!(params.clock.y, if monitor.alive { 1. } else { 0. });
    assert_eq!(params.clock.w, monitor.period());
    assert!(params.clock.x < monitor.period(), "the shared clock, wrapped by the period");
    press_f(&mut app);
    assert!(!ecg(&app) && app.world().get::<MeshMaterial3d<StandardMaterial>>(screen).is_some());
    press_f(&mut app);
    assert!(ecg(&app));
    app.world_mut().resource_mut::<GameSnapshot>().0 = grid(false);
    step(&mut app, 15);
    assert!(!ecg(&app), "dark with the grid");
}

fn pump(app: &mut App) {
    let state = {
        let bridge = app.world().resource::<CoreBridge>();
        let mut state = Value::Null;
        for _ in 0..12 {
            state = bridge.request("tick", json!({})).unwrap()["state"].clone();
        }
        state
    };
    app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
    let mut snapshot = state;
    snapshot["deployables"] = json!([]);
    app.world_mut().resource_mut::<GameSnapshot>().0 = snapshot;
}

/// The listen host buys a cola: the shake, then the server's can at the tray;
/// broke, the press only flashes the rim and asks the server for nothing.
#[test]
fn the_host_buys_a_can_through_the_server_and_a_broke_press_only_flashes() {
    let bridge = CoreBridge::launch(std::path::Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request("init", json!({"contentRoot":WorldConfig::default().content_root,"name":"vending","spawn":[0,0,0],"allowCheats":true}))
        .unwrap();
    let (mut app, prop) = prop_app("Vendor_0", 12, Vec3::new(270., 0., 0.), json!({"player":{"alive":true}}), UiModel::default());
    app.insert_resource(bridge);
    pump(&mut app);
    step(&mut app, 4);
    fn vendor(app: &App, prop: Entity) -> &vending::Vendor {
        app.world().get::<vending::Vendor>(prop).unwrap()
    }
    assert!(app.world().resource::<UiModel>().network.owns_world);
    assert_eq!(vendor(&app, prop).state, State::NoFunds);
    let info = app.world().resource::<crate::info_billboard::FocusedInfo>().0.clone().unwrap();
    assert_eq!(info.content.name, "Cola Machine");
    assert_eq!(info.content.prompt, "Insufficient funds -- $1 cola");
    assert_eq!(app.world().resource::<InteractionFocus>().outlined(), Some((prop, vending::NO_FUNDS)));
    press_f(&mut app);
    assert!(vendor(&app, prop).denied > 0., "a refusal flashes");
    assert_eq!(vendor(&app, prop).shake, 0.);
    app.world().resource::<CoreBridge>().request("action", json!({"name":"console","args":{"text":"give 1056"}})).unwrap();
    pump(&mut app);
    step(&mut app, 40);
    let dollars = |app: &App| -> u32 {
        app.world().resource::<UiModel>().pages.iter().filter(|p| p.index < 7).flat_map(|p| p.items.iter())
            .filter(|i| i.id == 1056).map(|i| u32::from(i.amount)).sum()
    };
    let before = dollars(&app);
    assert!(before >= 1);
    assert_eq!(vendor(&app, prop).state, State::Ready);
    let info = app.world().resource::<crate::info_billboard::FocusedInfo>().0.clone().unwrap();
    assert_eq!(info.content.prompt, "[F] $1 cola");
    let rest = vendor(&app, prop).rest.translation;
    press_f(&mut app);
    assert_eq!(vendor(&app, prop).state, State::Dispensing);
    assert!(app.world().get::<Transform>(prop).unwrap().translation.distance(rest) > 0., "the machine shudders");
    pump(&mut app);
    assert_eq!(dollars(&app), before - 1, "the server took one dollar");
    step(&mut app, 30);
    assert_eq!(app.world().get::<Transform>(prop).unwrap().translation, rest, "it lands exactly home");
    pump(&mut app);
    let tray = vendor(&app, prop).dispense_point();
    let can = app.world().resource::<GameSnapshot>().0["worldItems"].as_array().unwrap().iter()
        .find(|i| i["itemId"] == 80).cloned().expect("the server's can");
    let at = crate::player::vector(&can["position"]).unwrap();
    assert!(Vec2::new(at.x, at.z).distance(Vec2::new(tray.x, tray.z)) < 0.3, "{at} vs tray {tray}");
}
