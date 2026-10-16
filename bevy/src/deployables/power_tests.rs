//! Accepted power presentation: the procedural turbine, consumer lamps, generator
//! engine and look-at readouts all consume accepted rows and the accepted solve.
use super::*;
use crate::{content::ObjData, ui::UiState};
use bevy::time::TimeUpdateStrategy;
use tests::{fixture, initialize, tick};

const DT: f32 = 0.05;

fn timed(app: &mut App) {
    app.insert_resource(UiState {
        screen: crate::ui::Screen::Playing,
        ..default()
    })
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_secs_f32(DT),
    ));
    // The first frame has no delta under the manual strategy.
    app.update();
}
fn row(id: u32, def: u16, extra: Value) -> Value {
    let mut row = json!({"netId":id,"defId":def,"position":[0,5,-2],"yaw":0,
        "health":300,"fuel":0,"onFire":false,"toggledOn":false,"energy":0,"wind":0});
    for (k, v) in extra.as_object().unwrap() {
        row[k] = v.clone();
    }
    row
}
fn hub_angle(app: &mut App) -> f32 {
    let mut q = app.world_mut().query::<(&turbine::Hub, &Transform)>();
    let (_, pose) = q.single(app.world()).unwrap();
    let (axis, angle) = pose.rotation.to_axis_angle();
    if angle.abs() < 1e-6 {
        0.
    } else {
        angle * axis.z.signum()
    }
}

#[test]
fn source_turbine_body_seats_on_its_aabb_spins_with_accepted_wind_and_offers_its_socket() {
    let bridge = initialize();
    let mut state = tick(&bridge);
    state["deployables"] = json!([row(5001, 9106, json!({"wind":0.5,"position":[3,5,-2]}))]);
    let mut app = fixture(bridge, state);
    timed(&mut app);
    let mut bodies = app
        .world_mut()
        .query::<(Entity, &Deployed, &Transform, &Collider)>();
    let (root, deployed, pose, collider) = bodies.single(app.world()).unwrap();
    assert_eq!(
        (deployed.net_id, deployed.def_id),
        (5001, 9106),
        "the turbine is no longer skipped"
    );
    assert!(
        pose.translation.distance(Vec3::new(3., 5., -2.)) < 1e-5,
        "upright: base on the point, no stand-up lift"
    );
    let compound = collider.as_compound().unwrap();
    let (local, shape) = &compound.raw.shapes()[0];
    let (points, _) = shape.as_cuboid().unwrap().to_trimesh();
    let corners: Vec<Vec3> = points
        .into_iter()
        .map(|p| local.transform_point(p))
        .collect();
    let min = corners.iter().fold(Vec3::INFINITY, |a, b| a.min(*b));
    let max = corners.iter().fold(Vec3::NEG_INFINITY, |a, b| a.max(*b));
    assert!(
        min.distance(Vec3::new(-1.15, 0., -0.15)) < 1e-5
            && max.distance(Vec3::new(1.15, 3.75, 1.05)) < 1e-5,
        "BuildTurbine localAabb collider"
    );
    app.update();
    let mut sockets = app.world_mut().query::<(&ChildOf, &Transform, &Name)>();
    let socket = sockets
        .iter(app.world())
        .find(|(p, _, n)| p.parent() == root && n.as_str().contains("socket0"))
        .map(|(_, t, _)| t.translation)
        .expect("source Output port on the turbine");
    assert!(socket.distance(Vec3::new(0.16, 0.12, 0.)) < 1e-6);

    let before = hub_angle(&mut app);
    for _ in 0..4 {
        app.update();
    }
    let turned = hub_angle(&mut app) - before;
    assert!(
        (turned - 4. * DT * (0.25 + 5.5 * 0.5)).abs() < 1e-4,
        "spin = 0.25 + 5.5 x accepted wind ({turned})"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["deployables"][0]["wind"] = json!(0.);
    let before = hub_angle(&mut app);
    app.update();
    assert!(
        (hub_angle(&mut app) - before - DT * 0.25).abs() < 1e-5,
        "calm keeps the idle turn"
    );
    app.world_mut().resource_mut::<UiState>().screen = crate::ui::Screen::Paused;
    let before = hub_angle(&mut app);
    app.update();
    assert_eq!(
        hub_angle(&mut app),
        before,
        "singleplayer pause stops the hub"
    );
}

#[test]
fn lamp_lens_split_matches_unmodified_godot_objmesh_carving() {
    let bridge = initialize();
    let defs: Vec<Definition> = serde_json::from_value(
        bridge.request("catalog", json!({})).unwrap()["result"]["deployables"].clone(),
    )
    .unwrap();
    assert!(
        defs.iter()
            .filter(|d| !d.lights.is_empty())
            .all(|d| [459, 1222].contains(&d.id))
    );
    // bevy/tools/lamp_lens_reference.py: actual Godot ObjMesh.Load + SplitByUvAndPos. The source
    // comment's "16 head triangles" is stale; the runtime carves the 4 amber glass faces.
    let rows: Value =
        serde_json::from_str(include_str!("../../data/lamp-lens-source-reference.json")).unwrap();
    for case in rows.as_array().unwrap() {
        let def = defs
            .iter()
            .find(|d| Some(d.id as u64) == case["id"].as_u64())
            .unwrap();
        let model = def.model.as_ref().unwrap();
        let data = ObjData::load(
            &WorldConfig::default()
                .content_root
                .join(format!("objects/{model}.obj")),
        )
        .unwrap();
        assert_eq!(
            data.positions.len() / 3,
            case["triangles"].as_u64().unwrap() as usize,
            "{model}"
        );
        let (body, lens) = lamps::split_lens(def, &data).expect("source lamp glass");
        let expected = case["lens"].as_array().unwrap();
        assert_eq!(
            lens.positions.len() / 3,
            expected.len(),
            "{model} lens triangles"
        );
        for (triangle, centroid) in lens.positions.chunks_exact(3).zip(expected) {
            let actual = triangle.iter().map(|p| Vec3::from_array(*p)).sum::<Vec3>() / 3.;
            assert!(
                actual.distance(crate::player::vector(centroid).unwrap()) < 1e-5,
                "{model} {actual}"
            );
        }
        assert_eq!(
            body.positions.len() + lens.positions.len(),
            data.positions.len()
        );
    }
}

#[test]
fn lens_carve_needs_both_the_glass_texel_and_a_lamp_within_bulb_reach() {
    // Real content never puts glass texels outside the heads, so BulbReach is
    // guarded synthetically: glass far from a lamp and other texels stay body.
    let def: Definition = serde_json::from_value(json!({"id":1,"name":"Probe","size":{"x":1,"y":1,"z":1},
        "offset":0,"upright":false,"mount":"Floor","fixture":"None","isStorage":false,"isWindTurbine":false,
        "procBox":false,"meshEuler":{"x":0,"y":0,"z":0},
        "lights":[{"spot":false,"pos":{"x":0,"y":0,"z":0},"range":1,"energy":1,"color":{"r":1,"g":1,"b":1}}]}))
    .unwrap();
    let mut data = ObjData::default();
    for (center, uv) in [
        (Vec3::ZERO, [0.75, 0.75]),
        (Vec3::X * 0.43, [0.75, 0.75]),
        (Vec3::Z * 0.1, [0.75, 0.25]),
    ] {
        for corner in [Vec3::ZERO, Vec3::X * 0.01, Vec3::Y * 0.01] {
            data.positions
                .push((center + corner - Vec3::new(0.01, 0.01, 0.) / 3.).to_array());
            data.normals.push([0., 0., 1.]);
            data.uvs.push(uv);
            data.colors.push([1.; 4]);
        }
    }
    let (body, lens) = lamps::split_lens(&def, &data).unwrap();
    assert_eq!(
        lens.positions.len(),
        3,
        "only the glass texel within 0.42 m glows"
    );
    assert_eq!(body.positions.len(), 6);
}

fn place(bridge: &CoreBridge, def: u16, x: f32) {
    bridge
        .request(
            "action",
            json!({"name":"console","args":{"text":format!("give {def}")}}),
        )
        .unwrap();
    tick(bridge);
    bridge
        .request(
            "action",
            json!({"name":"placeDeployable","args":{"defId":def,"pos":[x,5,-2],"yawDegrees":0}}),
        )
        .unwrap();
    tick(bridge);
}
fn lamp_state(app: &mut App) -> (f32, bool, Vec<(Visibility, f32)>) {
    let mut q = app.world_mut().query::<&lamps::Lamps>();
    let lamps = q.single(app.world()).unwrap();
    let (level, shown) = (lamps.level, lamps.shown);
    let mut lights = app.world_mut().query::<(&Visibility, &SpotLight)>();
    let lights = lights
        .iter(app.world())
        .map(|(v, s)| (*v, s.intensity))
        .collect();
    (level, shown, lights)
}

#[test]
fn accepted_consumer_power_drives_the_source_lamp_envelope_lights_and_bulb_not_a_pending_toggle() {
    let bridge = initialize();
    place(&bridge, 458, -1.5);
    place(&bridge, 459, 1.5);
    let state = tick(&bridge);
    let id = |def| {
        state["deployables"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["defId"] == def)
            .unwrap()["netId"]
            .as_u64()
            .unwrap() as u32
    };
    let (generator, lamp) = (id(458), id(459));
    let mut app = fixture(bridge, state);
    timed(&mut app);
    let pump = |app: &mut App| {
        let state = tick(app.world().resource::<CoreBridge>());
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&state);
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    };
    let request = |app: &mut App, name: &str, args: Value| {
        app.world()
            .resource::<CoreBridge>()
            .request("action", json!({"name":name,"args":args}))
            .unwrap();
    };
    request(
        &mut app,
        "connectWire",
        json!({"srcId":generator,"srcPort":0,"dstId":lamp,"dstPort":0}),
    );
    request(
        &mut app,
        "toggleDeployable",
        json!({"netId":generator,"on":true}),
    );
    app.update();
    let (level, _, lights) = lamp_state(&mut app);
    assert_eq!(lights.len(), 2, "two source spots, one per head");
    assert_eq!(level, 0., "a queued toggle and wire never light the lamp");
    assert!(
        lights
            .iter()
            .all(|(v, i)| *v == Visibility::Hidden && *i == 0.)
    );

    pump(&mut app);
    app.update();
    let (first, _, _) = lamp_state(&mut app);
    assert!(
        (first - DT / 1.3).abs() < 1e-5,
        "accepted power starts the 1.3 s warm-up ({first})"
    );
    for _ in 0..30 {
        app.update();
    }
    let (level, shown, lights) = lamp_state(&mut app);
    assert_eq!(level, 1.);
    assert!(shown);
    for (visibility, intensity) in &lights {
        assert_eq!(*visibility, Visibility::Inherited);
        assert!(
            (intensity - crate::hdr::point_lumens(9.)).abs() < 1e-2,
            "steady state is the source energy 9"
        );
    }
    // Basis.LookingAt(Dir, Back) on the stood-up body: -Z throws along the authored beam.
    let mut q = app.world_mut().query::<(&GlobalTransform, &SpotLight)>();
    let mut bodies = app.world_mut().query::<(&Deployed, &GlobalTransform)>();
    let body = bodies
        .iter(app.world())
        .find(|(d, _)| d.net_id == lamp)
        .unwrap()
        .1
        .compute_transform();
    let beam = (body.rotation * Vec3::new(0., -0.966, 0.259)).normalize();
    for (pose, _) in q.iter(app.world()) {
        assert!(pose.forward().as_vec3().distance(beam) < 1e-4);
    }
    let mut lens = app
        .world_mut()
        .query::<(&Name, &MeshMaterial3d<StandardMaterial>)>();
    let handle = lens
        .iter(app.world())
        .find(|(n, _)| n.as_str() == "LampLens")
        .unwrap()
        .1
        .0
        .clone();
    let lit = app
        .world()
        .resource::<Assets<StandardMaterial>>()
        .get(&handle)
        .unwrap()
        .clone();
    assert!((lit.perceptual_roughness - 0.4).abs() < 1e-6);
    let hot = Color::srgb(
        0.9706 + 0.0294 * 0.75,
        0.9612 + 0.0388 * 0.75,
        0.835 + 0.165 * 0.75,
    );
    assert!(
        (lit.emissive - hot.to_linear() * 6.).to_vec4().length() < 1e-3,
        "white-hot bulb at LensEmission 6"
    );

    request(
        &mut app,
        "toggleDeployable",
        json!({"netId":generator,"on":false}),
    );
    app.update();
    assert_eq!(
        lamp_state(&mut app).0,
        1.,
        "the off request alone dims nothing"
    );
    pump(&mut app);
    app.update();
    let (level, _, _) = lamp_state(&mut app);
    assert!(
        (level - (1. - DT / 1.1)).abs() < 1e-5,
        "accepted loss cools down over 1.1 s"
    );
    for _ in 0..30 {
        app.update();
    }
    let (level, shown, lights) = lamp_state(&mut app);
    assert_eq!(level, 0.);
    assert!(!shown && lights.iter().all(|(v, _)| *v == Visibility::Hidden));
    let off = lens
        .iter(app.world())
        .find(|(n, _)| n.as_str() == "LampLens")
        .unwrap()
        .1
        .0
        .clone();
    assert_ne!(
        off, handle,
        "a dark bulb returns to the fixture's own material"
    );
}

fn graph(devices: Value) -> wires::Accepted {
    let mut accepted = wires::Accepted::default();
    accepted.graph = Some(
        serde_json::from_value(
            json!({"version":1,"readOnly":true,"stateHash":1,"wires":[],"devices":devices}),
        )
        .unwrap(),
    );
    accepted
}

#[test]
fn look_at_readouts_follow_accepted_fuel_charge_wind_and_solved_load() {
    let bridge = initialize();
    let defs: Vec<Definition> = serde_json::from_value(
        bridge.request("catalog", json!({})).unwrap()["result"]["deployables"].clone(),
    )
    .unwrap();
    let def = |id| defs.iter().find(|d| d.id == id).unwrap();
    let accepted = graph(json!([
        {"netId":1,"ports":[{"index":0,"live":4000,"powered":false,"draw":250}]},
        {"netId":2,"ports":[{"index":1,"live":600,"powered":false,"draw":250}]},
    ]));
    let generator = interaction::readout(
        def(458),
        &row(1, 458, json!({"fuel":150000,"toggledOn":true})),
        450.,
        String::new(),
        &accepted,
    );
    // 100 mL/s x (0.2 + 0.8 x 250/4000) = 25 mL/s -> 6000 s.
    assert_eq!(generator.name, "Generator  · 1h 40m");
    assert_eq!(generator.bars[1].unwrap().value, 1.);
    assert!((generator.bars[2].unwrap().value - 0.0625).abs() < 1e-6);
    let battery = interaction::readout(
        def(1450),
        &row(2, 1450, json!({"energy":1_080_000})),
        200.,
        String::new(),
        &accepted,
    );
    assert_eq!(battery.name, "Vehicle Battery  · 1h 12m");
    assert_eq!(
        battery.bars[1].unwrap().value,
        0.5,
        "charge bar in the fuel slot"
    );
    assert!((battery.bars[2].unwrap().value - 250. / 600.).abs() < 1e-6);
    let wind = interaction::readout(
        def(9106),
        &row(3, 9106, json!({"wind":0.6})),
        300.,
        String::new(),
        &accepted,
    );
    assert_eq!(wind.name, "Wind Turbine  · 1500w");
    assert_eq!(wind.bars[1].unwrap().value, 0.6);
    assert_eq!(
        wind.bars[1].unwrap().color,
        Color::srgb(0x35 as f32 / 255., 0xc0 as f32 / 255., 0xe0 as f32 / 255.),
        "LoadColor wind bar"
    );
    assert!(wind.bars[2].is_none(), "no load bar on the turbine");
    let idle = interaction::readout(
        def(458),
        &row(1, 458, json!({"fuel":150000})),
        450.,
        String::new(),
        &accepted,
    );
    assert_eq!(
        idle.name, "Generator",
        "an untoggled generator shows no runtime"
    );
    let lamp = interaction::readout(
        def(459),
        &row(4, 459, json!({})),
        300.,
        String::new(),
        &accepted,
    );
    assert_eq!(
        lamp.bars.iter().flatten().count(),
        1,
        "a consumer shows only health"
    );
}

#[test]
fn generator_engine_voice_and_shake_follow_the_accepted_run_target() {
    let bridge = initialize();
    let mut state = tick(&bridge);
    state["deployables"] = json!([row(
        6001,
        458,
        json!({"fuel":150000,"toggledOn":true,"health":450})
    )]);
    let mut app = fixture(bridge, state);
    app.init_asset::<bevy::audio::AudioSource>();
    timed(&mut app);
    for _ in 0..3 {
        app.update();
    }
    let level = app.world().resource::<engine::Ramps>().levels[&6001];
    assert!((level - 3. * DT / 1.3).abs() < 1e-5);
    let mut voices = app
        .world_mut()
        .query::<(&engine::Voice, &PlaybackSettings)>();
    let (_, playback) = voices
        .single(app.world())
        .expect("looping Engine voice while spun up");
    assert!(matches!(playback.mode, bevy::audio::PlaybackMode::Loop));
    assert!(
        (playback.speed - (0.6 + 0.4 * level)).abs() < 1e-5,
        "unloaded spin-up pitch"
    );
    let mut meshes = app
        .world_mut()
        .query::<(&presentation::BodyMesh, &Transform)>();
    assert!(
        meshes.single(app.world()).unwrap().1.translation.length() > 0.,
        "running body shakes"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["deployables"][0]["onFire"] = json!(true);
    app.update();
    assert_eq!(
        app.world().resource::<engine::Ramps>().levels[&6001],
        0.,
        "a dying generator cuts out instantly"
    );
    app.update();
    assert_eq!(voices.iter(app.world()).count(), 0);
    assert_eq!(
        meshes.single(app.world()).unwrap().1.translation,
        Vec3::ZERO
    );
}

#[test]
fn accepted_grid_fixture_is_a_port_endpoint_that_wires_a_lamp_through_the_server_mains() {
    let bridge = initialize();
    let registered = bridge
        .request(
            "worldServices",
            json!({"fixtures":[{"defId":9200,"position":[0,5,-2],"yaw":30}]}),
        )
        .unwrap();
    let grid = registered["result"]["fixtures"][0]["netId"]
        .as_u64()
        .unwrap() as u32;
    place(&bridge, 459, 3.);
    let state = tick(&bridge);
    let lamp = state["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["defId"] == 459)
        .unwrap()["netId"]
        .as_u64()
        .unwrap() as u32;
    let mut app = fixture(bridge, state);
    timed(&mut app);
    app.update();
    let mut roots = app
        .world_mut()
        .query::<(Entity, &Deployed, &Transform, Has<Collider>, Has<Mesh3d>)>();
    let (root, _, pose, collider, mesh) = roots
        .iter(app.world())
        .find(|(_, d, ..)| d.net_id == grid)
        .map(|(e, d, t, c, m)| (e, d.def_id, *t, c, m))
        .expect("accepted mains fixture replica");
    assert!(
        !collider && !mesh,
        "the map object owns the breaker box mesh and collision"
    );
    // The accepted yaw is on the wire's yaw grid, not the requested 30 degrees.
    let yaw = app.world().resource::<GameSnapshot>().0["deployables"]
        .as_array()
        .unwrap()
        .iter()
        .find(|r| r["netId"] == grid)
        .unwrap()["yaw"]
        .as_f64()
        .unwrap() as f32;
    assert!((yaw - 30.).abs() < 1.5);
    let expected =
        Quat::from_rotation_y(yaw.to_radians()) * Quat::from_rotation_x((-90_f32).to_radians());
    for axis in [Vec3::X, Vec3::Y, Vec3::Z] {
        assert!(
            (pose.rotation * axis).distance(expected * axis) < 1e-5,
            "Materialize basis {axis}"
        );
    }
    assert!(pose.translation.distance(Vec3::new(0., 5., -2.)) < 1e-5);
    assert!(
        !app.world()
            .resource::<Replicas>()
            .published
            .contains_key(&grid),
        "a fixture never uploads deployable authority geometry"
    );
    assert!(
        app.world()
            .resource::<Replicas>()
            .published
            .contains_key(&lamp)
    );
    let mut sockets = app.world_mut().query::<(&ChildOf, &Transform, &Name)>();
    let socket = sockets
        .iter(app.world())
        .find(|(p, _, n)| p.parent() == root && n.as_str().contains("socket0"))
        .map(|(_, t, _)| t.translation)
        .expect("source PortLocal output");
    assert!(socket.distance(Vec3::new(0.32, 0.18, 0.933)) < 1e-6);

    let pump = |app: &mut App| {
        let state = tick(app.world().resource::<CoreBridge>());
        app.world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&state);
        app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        app.update();
    };
    let request = |app: &mut App, args: Value| {
        app.world()
            .resource::<CoreBridge>()
            .request("action", args)
            .unwrap();
    };
    request(
        &mut app,
        json!({"name":"connectWire","args":{"srcId":grid,"srcPort":0,"dstId":lamp,"dstPort":0}}),
    );
    pump(&mut app);
    app.update();
    let mut segments = app.world_mut().query::<(&wires::WireSegment, &Transform)>();
    let (_, wire) = segments
        .single(app.world())
        .expect("accepted wire from the mains endpoint");
    let end = pose.transform_point(Vec3::new(0.32, 0.18, 0.933));
    assert!(
        wire.transform_point(Vec3::Y * -0.5).distance(end) < 2e-5,
        "wire leaves the fixture's own socket"
    );
    for _ in 0..30 {
        app.update();
    }
    assert_eq!(
        lamp_state(&mut app).0,
        1.,
        "mains on by default: the lamp warms to full"
    );
    request(
        &mut app,
        json!({"name":"console","args":{"text":"toggleglobalpower"}}),
    );
    pump(&mut app);
    for _ in 0..30 {
        app.update();
    }
    assert_eq!(lamp_state(&mut app).0, 0., "accepted mains-off darkens it");
}
