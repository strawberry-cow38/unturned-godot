//! Source spraypaint / spare tire / carjack timelines, the host shove and the
//! accepted respray, plus round trips through a real local authority.
use super::*;
use crate::{
    player::WorldSessionEpoch,
    ui::{Screen, UiState},
    vehicle::{data, registration, tests::{body, physics_app}},
};
use bevy::{ecs::system::RunSystemOnce, input::mouse::MouseButtonInput};
use std::path::Path;

const CAN: u16 = 1840; // vehicle_paints.tsv: Midnight Black 0a0a0a
const SPARE: u16 = 1451;
const JACK: u16 = 277;
fn cell(id: u16) -> Cell {
    Cell { id, page: 2, x: 0, y: 0 }
}
fn tool(kind: Kind, seconds: f32, fraction: f32) -> Tool {
    Tool { kind, use_seconds: seconds, apply_fraction: fraction, paint_rgb: (kind == Kind::Paint).then_some(0x0a0a0a) }
}
fn car(id: u32) -> Target {
    Target { net_id: id, position: Vec3::Z, jackable: Some(true), ..default() }
}
/// Steps `seconds` of 20 ms frames, returning every outcome.
fn run(tools: &mut HandTools, held: Option<(Cell, Tool)>, look: Option<Target>, seconds: f32) -> Vec<Outcome> {
    let mut all = Vec::new();
    for _ in 0..(seconds / 0.02).round() as usize {
        all.extend(tools.step(0.02, held, false, look, |id| look.filter(|v| v.net_id == id)).0);
    }
    all
}
fn sends(outcomes: &[Outcome]) -> Vec<&'static str> {
    outcomes.iter().filter_map(|o| if let Outcome::Send(n, _) = o { Some(*n) } else { None }).collect()
}

#[test]
fn spray_lands_at_85_percent_on_the_vehicle_aimed_at_then_and_holds_the_hand_for_the_clip() {
    let held = Some((cell(CAN), tool(Kind::Paint, 2.8, 0.85)));
    let mut tools = HandTools::default();
    // TrySprayVehicle starts the sweep with nothing aimed at.
    let (start, _) = tools.step(0., held, true, None, |_| None);
    assert_eq!(start, vec![Outcome::Swing { kind: Kind::Paint, item: CAN, at: None }]);
    assert!(sends(&run(&mut tools, held, Some(car(7)), 2.36)).is_empty(), "nothing before 85% of 2.8 s");
    let landed = run(&mut tools, held, Some(car(7)), 0.04);
    assert_eq!(landed, vec![Outcome::Send("paintVehicle", json!({"netId":7,"itemId":CAN}))]);
    assert!(tools.busy(), "isUseable: the hand stays busy to the end of the clip");
    assert!(tools.step(0.02, held, true, Some(car(7)), |_| None).0.is_empty(), "no second sweep mid-clip");
    run(&mut tools, held, Some(car(7)), 0.42);
    assert!(!tools.busy());
    // ApplySpray re-validates: a wreck or empty aim at 85% sends nothing.
    tools.step(0., held, true, None, |_| None);
    let wreck = Target { wreck: true, ..car(7) };
    assert_eq!(run(&mut tools, held, Some(wreck), 2.4), vec![Outcome::Log("[paint] that one is a burnt-out wreck")]);
    // Switching away mid-sweep drops the pending paint.
    let mut tools = HandTools::default();
    tools.step(0., held, true, None, |_| None);
    run(&mut tools, held, Some(car(7)), 1.);
    assert_eq!(tools.step(0.02, None, false, Some(car(7)), |_| None), (vec![], true));
    assert!(sends(&run(&mut tools, held, Some(car(7)), 2.)).is_empty());
}

#[test]
fn spare_validates_at_the_click_and_goes_on_at_75_percent_of_the_same_car() {
    let held = Some((cell(SPARE), tool(Kind::Tire, 2.8, 0.75)));
    let mut tools = HandTools::default();
    let flat = Target { popped: 0b0110, ..car(9) };
    for (look, alert) in [
        (None, "Aim at a vehicle."),
        (Some(Target { wreck: true, ..flat }), "That one's a wreck."),
        (Some(Target { speed_sq: 1., ..flat }), "Stop it moving first."),
        (Some(car(9)), "No flat tires on that."),
    ] {
        assert_eq!(tools.step(0., held, true, look, |_| None).0, vec![Outcome::Notice(alert)]);
        assert!(!tools.busy(), "{alert}: refused before the swing");
    }
    tools.step(0., held, true, Some(flat), |_| None);
    // TickTire re-reads the car the spare was started on, not the aim.
    let mut fitted = Vec::new();
    for _ in 0..104 {
        fitted.extend(tools.step(0.02, held, false, None, |id| (id == 9).then_some(flat)).0);
    }
    assert!(sends(&fitted).is_empty(), "nothing before 75% of 2.8 s");
    for _ in 0..2 {
        fitted.extend(tools.step(0.02, held, false, None, |id| (id == 9).then_some(flat)).0);
    }
    assert_eq!(fitted, vec![Outcome::Send("fitTire", json!({"vehicleNetId":9,"wheelIndex":1}))]);
    assert!(!tools.busy(), "the source pending timer ends with the fit");
    // Driven off or fixed meanwhile: TickTire returns silently.
    tools.step(0., held, true, Some(flat), |_| None);
    let moving = Target { speed_sq: 4., ..flat };
    let mut late = Vec::new();
    for _ in 0..110 {
        late.extend(tools.step(0.02, held, false, None, |_| Some(moving)).0);
    }
    assert!(late.is_empty());
}

#[test]
fn carjack_refuses_before_the_wind_up_and_lifts_at_75_percent_of_the_crank() {
    let held = Some((cell(JACK), tool(Kind::Carjack, 1.967, 0.75)));
    let mut tools = HandTools::default();
    assert_eq!(tools.step(0., held, true, None, |_| None).0, vec![Outcome::Log("[carjack] aim at a vehicle")]);
    let flying = Target { jackable: Some(false), ..car(4) };
    assert_eq!(
        tools.step(0., held, true, Some(flying), |_| None).0,
        vec![Outcome::Log("[carjack] it is still in the air -- let it come down")]
    );
    // A puppet (no local physics) is not pre-tested, as in the source MP path.
    let puppet = Target { jackable: None, wreck: true, ..car(4) };
    assert!(matches!(tools.step(0., held, true, Some(puppet), |_| None).0[..], [Outcome::Swing { .. }]));
    let mut tools = HandTools::default();
    let (start, _) = tools.step(0., held, true, Some(car(4)), |_| None);
    assert_eq!(start, vec![Outcome::Swing { kind: Kind::Carjack, item: JACK, at: Some(Vec3::Z) }]);
    assert!(sends(&run(&mut tools, held, Some(car(4)), 1.46)).is_empty());
    assert_eq!(sends(&run(&mut tools, held, Some(car(4)), 0.04)), vec!["carjack"]);
    assert!(tools.busy() && tools.step(0.02, held, true, Some(car(4)), |_| None).0.is_empty(), "no spamming");
    run(&mut tools, held, Some(car(4)), 0.5);
    assert!(!tools.busy());
    let occupied = Target { occupied: true, ..car(4) };
    tools.step(0., held, true, Some(occupied), |_| None);
    assert_eq!(run(&mut tools, held, Some(occupied), 1.5), vec![Outcome::Log("[carjack] somebody is in it")]);
}

#[test]
fn jackable_is_a_dwell_then_not_rising_then_near_ground() {
    assert!(jackable(0.5, 9., || unreachable!()), "settled 0.5 s: wedged cars pass");
    assert!(!jackable(0.48, 1.01, || 0.), "on its way up");
    assert!(jackable(0., 1., || 3.));
    assert!(!jackable(0., 0., || 3.01));
    // Retail dv = F/2*0.02 = F*0.01 for any real mass.
    let (lin, ang) = carjack_impulse(Vec3::new(10., 512., -10.), Vec3::splat(64.), 1700.);
    assert!((lin / 1700. - Vec3::new(0.1, 5.12, -0.1)).length() < 1e-4);
    assert!((ang / 1700. - Vec3::splat(0.64)).length() < 1e-4);
}

fn jack_app() -> (App, Entity) {
    let mut app = physics_app(None);
    app.init_resource::<PendingJacks>()
        .add_systems(PostUpdate, apply_jacks.after(crate::vehicle::step_bodies).before(PhysicsSet::SyncBackend));
    app.world_mut().spawn((Collider::cuboid(500., 0.5, 500.), Transform::from_xyz(0., -0.5, 0.)));
    let car = body(&mut app, "jeep", Vec3::new(0., 1.8, 0.));
    {
        let mut b = app.world_mut().get_mut::<VehicleBody>(car).unwrap();
        b.driver_player_id = 0;
        b.controls = default();
    }
    for _ in 0..150 {
        app.update();
    }
    (app, car)
}
fn rise(app: &mut App, car: Entity) -> f32 {
    app.world_mut().resource_mut::<PendingJacks>().0.push((1, Vec3::Y * 512., Vec3::ZERO));
    let mut peak = 0f32;
    for _ in 0..5 {
        app.update();
        peak = peak.max(app.world().get::<Velocity>(car).unwrap().linear.y);
    }
    peak
}

#[test]
fn the_physics_owner_lifts_an_empty_settled_car_by_retails_dv_and_refuses_the_rest() {
    let (mut app, car) = jack_app();
    assert!(app.world().get::<JackState>(car).unwrap().slow_for >= JACK_SETTLED_DWELL);
    let lift = rise(&mut app, car);
    assert!((4.4..=5.2).contains(&lift), "F*0.01 = 5.12 m/s upward less a step of gravity: {lift}");
    assert!(rise(&mut app, car) < lift - 0.5, "rising past 1 m/s: a re-jack mid-flight is refused");
    let (mut app, car) = jack_app();
    app.world_mut().get_mut::<VehicleBody>(car).unwrap().passengers = vec![3];
    assert!(rise(&mut app, car) < 0.5, "Carjack: !isEmpty -> refused");
    let (mut app, car) = jack_app();
    app.world_mut().get_mut::<VehicleBody>(car).unwrap().flags |= 32;
    assert!(rise(&mut app, car) < 0.5, "a wreck is never jacked");
}

#[test]
fn accepted_respray_swaps_only_the_body_material_and_bakes_paintable_texels() {
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), TransformPlugin))
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .insert_resource(config.clone())
        .init_resource::<ContentCache>();
    let sedan = data::named("sedan").unwrap();
    let spawn = app.world_mut().resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial::default());
    let other = app.world_mut().resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial::default());
    let e = body(&mut app, "sedan", Vec3::ZERO);
    app.world_mut().entity_mut(e).insert(BodyPaint { material: spawn.clone(), rgb: None });
    let panel = app.world_mut().spawn((MeshMaterial3d(spawn.clone()), ChildOf(e))).id();
    let nested = app.world_mut().spawn(ChildOf(panel)).id();
    app.world_mut().entity_mut(nested).insert(MeshMaterial3d(spawn.clone()));
    let lamp = app.world_mut().spawn((MeshMaterial3d(other.clone()), ChildOf(e))).id();
    app.world_mut().run_system_once(repaint).unwrap();
    assert_eq!(app.world().get::<MeshMaterial3d<StandardMaterial>>(panel).unwrap().0, spawn, "unsprayed: spawn paint stands");
    app.world_mut().get_mut::<VehicleBody>(e).unwrap().paint_rgb = Some(0x25c891);
    app.world_mut().run_system_once(repaint).unwrap();
    let painted = app.world().get::<MeshMaterial3d<StandardMaterial>>(panel).unwrap().0.clone();
    assert_ne!(painted, spawn);
    assert_eq!(app.world().get::<MeshMaterial3d<StandardMaterial>>(nested).unwrap().0, painted);
    assert_eq!(app.world().get::<MeshMaterial3d<StandardMaterial>>(lamp).unwrap().0, other, "fixed parts keep theirs");
    assert_eq!(app.world().get::<BodyPaint>(e).unwrap().rgb, Some(0x25c891));
    // vehicle_paint.gdshader: alpha-0 texels take the paint, opaque ones keep the palette.
    let source = image::open_rgba(&config.content_root.join(sedan.spec.palette.as_ref().unwrap()));
    let material = app.world().resource::<Assets<StandardMaterial>>().get(&painted).unwrap();
    let texture = material.base_color_texture.clone().unwrap();
    let baked = app.world().resource::<Assets<Image>>().get(&texture).unwrap().data.clone().unwrap();
    let (mut paintable, mut fixed) = (0, 0);
    for (src, out) in source.chunks_exact(4).zip(baked.chunks_exact(4)) {
        if src[3] < 128 {
            assert_eq!(out, [0x25, 0xc8, 0x91, 255]);
            paintable += 1;
        } else {
            assert_eq!(out, src);
            fixed += 1;
        }
    }
    assert!(paintable > 0 && fixed > 0, "the sedan palette has both: {paintable}/{fixed}");
    // A second car sprayed the same colour shares the cached bake.
    let again = body_material(sedan, paint_colour(0x25c891), &format!("vehicle:{}:rgb:25c891", sedan.key),
        &config.content_root, &mut app.world_mut().resource_mut::<ContentCache>(), &mut Assets::default(), &mut Assets::default());
    assert_eq!(again, painted);
}
mod image {
    pub fn open_rgba(path: &std::path::Path) -> Vec<u8> {
        let bytes = std::fs::read(path).unwrap();
        let image = bevy::image::Image::from_buffer(
            &bytes,
            bevy::image::ImageType::Extension("png"),
            bevy::image::CompressedImageFormats::NONE,
            true,
            bevy::image::ImageSampler::Default,
            bevy::asset::RenderAssetUsages::default(),
        )
        .unwrap();
        image.data.unwrap()
    }
}

// ---- Round trips through a real local authority --------------------------------------------

struct Rig {
    app: App,
    window: Entity,
    car: u32,
    entity: Entity,
}
fn tick(app: &mut App) -> Value {
    let mut state = Value::Null;
    for _ in 0..12 {
        state = app.world().resource::<CoreBridge>().request("tick", json!({})).unwrap()["state"].clone();
    }
    app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
    app.world_mut().run_system_once(crate::vehicle::sync_vehicles).unwrap();
    state
}
fn rig() -> Rig {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init", json!({"contentRoot":WorldConfig::default().content_root,"name":"hand-tools","spawn":[3,0,1],"allowCheats":true})).unwrap();
    let sedan = data::named("sedan").unwrap();
    let fleet = bridge.request("worldVehicles", json!({"vehicles":[registration(sedan, "hand tools".into(), Vec3::ZERO, 0., 0)]})).unwrap();
    let car = fleet["result"]["vehicles"][0]["netId"].as_u64().unwrap() as u32;
    for id in [CAN, SPARE, JACK] {
        bridge.request("action", json!({"name":"console","args":{"text":format!("give {id}")}})).unwrap();
    }
    // No native `simulate` runs here, so place the authority's player beside the car.
    bridge.request("action", json!({"name":"console","args":{"text":"teleport 3 0 1"}})).unwrap();
    let visuals: Vec<ItemVisual> = serde_json::from_value(bridge.request("catalog", json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = visuals.into_iter().map(|i| (i.id, i)).collect();
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), TransformPlugin))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .init_asset::<AudioSource>()
        .insert_resource(bridge)
        .insert_resource(catalog)
        .insert_resource(GameSnapshot(Value::Null))
        .init_resource::<UiModel>()
        .insert_resource(UiState { screen: Screen::Playing, ..default() })
        .init_resource::<Settings>()
        .init_resource::<FrameInput>()
        .init_resource::<WorldSessionEpoch>()
        .insert_resource(WorldConfig::default())
        .init_resource::<ContentCache>()
        .init_resource::<crate::vehicle::Vehicles>()
        .init_resource::<HandTools>()
        .init_resource::<PendingJacks>()
        .add_message::<HandEvent>()
        .add_message::<CoreEvent>()
        .add_message::<crate::ui::UiIntent>()
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .add_systems(Update, (input, read_jacks).chain());
    let window = app.world_mut().spawn((PrimaryWindow, Window { focused: true, ..default() },
        CursorOptions { visible: false, grab_mode: CursorGrabMode::Locked, ..default() })).id();
    let entity = body(&mut app, "sedan", Vec3::ZERO);
    {
        let mut b = app.world_mut().get_mut::<VehicleBody>(entity).unwrap();
        b.net_id = car;
        b.driver_player_id = 0;
    }
    app.world_mut().resource_mut::<crate::vehicle::Vehicles>().entities.insert(car, entity);
    app.insert_resource(LookedVehicle(Some(entity)));
    let mut rig = Rig { app, window, car, entity };
    tick(&mut rig.app);
    rig
}
fn hold(rig: &mut Rig, id: u16) {
    let state = tick(&mut rig.app);
    let cell = state["inventory"]["pages"].as_array().unwrap().iter().find_map(|p| {
        p["items"].as_array().unwrap().iter().find(|i| i["id"] == id).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
    }).unwrap();
    rig.app.world().resource::<CoreBridge>().request("action", json!({"name":"holdItem","args":{"page":cell.0,"x":cell.1,"y":cell.2}})).unwrap();
    tick(&mut rig.app);
}
fn click(rig: &mut Rig) {
    let window = rig.window;
    rig.app.world_mut().resource_mut::<FrameInput>().push(WindowEvent::MouseButtonInput(MouseButtonInput {
        button: MouseButton::Left,
        state: ButtonState::Pressed,
        window,
    }));
    step(rig, 1);
}
fn step(rig: &mut Rig, n: usize) {
    for _ in 0..n {
        rig.app.update();
        rig.app.world_mut().resource_mut::<FrameInput>().begin();
    }
}
fn row(rig: &mut Rig) -> Value {
    let car = rig.car;
    tick(&mut rig.app)["vehicles"].as_array().unwrap().iter().find(|r| r["netId"] == car).unwrap().clone()
}
fn count(state: &Value, id: u16) -> u64 {
    state["inventory"]["pages"].as_array().unwrap().iter()
        .flat_map(|p| p["items"].as_array().unwrap().iter())
        .filter(|i| i["id"] == id).map(|i| i["amount"].as_u64().unwrap()).sum()
}

#[test]
fn held_spraypaint_resprays_the_aimed_car_through_the_authority() {
    let mut rig = rig();
    hold(&mut rig, CAN);
    click(&mut rig);
    step(&mut rig, 117); // < 0.85 * 2.8 s
    assert!(row(&mut rig)["paintRgb"].is_null(), "no optimistic or early paint");
    step(&mut rig, 4);
    let accepted = row(&mut rig);
    assert_eq!(accepted["paintRgb"], 0x0a0a0a, "the can's colour, accepted and replicated");
    assert_eq!(rig.app.world().get::<VehicleBody>(rig.entity).unwrap().paint_rgb, Some(0x0a0a0a), "adopted by the native body");
    let state = tick(&mut rig.app);
    assert_eq!(count(&state, CAN), 0, "the server spent the can");
}

#[test]
fn held_spare_refits_a_flat_and_a_held_jack_reaches_the_physics_owner() {
    let mut rig = rig();
    hold(&mut rig, SPARE);
    click(&mut rig);
    assert_eq!(rig.app.world().resource::<UiModel>().notices.last().map(String::as_str), Some("No flat tires on that."));
    let car = rig.car;
    rig.app.world().resource::<CoreBridge>().request("worldVehicleDamage", json!({"netId":car,"bodyPart":"tire:1","amount":50})).unwrap();
    let popped = row(&mut rig)["poppedTireMask"].as_u64().unwrap();
    assert_eq!(popped, 2, "the host popped tire 1");
    assert_eq!(rig.app.world().get::<VehicleBody>(rig.entity).unwrap().popped_tire_mask, 2);
    click(&mut rig);
    step(&mut rig, 103); // < 0.75 * 2.8 s
    assert_eq!(row(&mut rig)["poppedTireMask"], 2);
    step(&mut rig, 3);
    assert_eq!(row(&mut rig)["poppedTireMask"], 0, "the wheel is back on");
    assert_eq!(count(&tick(&mut rig.app), SPARE), 0, "and the spare spent");

    // No physics world here: the host's apply_jacks dwell counter says parked.
    let entity = rig.entity;
    rig.app.world_mut().entity_mut(entity).insert(JackState { slow_for: 1. });
    hold(&mut rig, JACK);
    click(&mut rig);
    step(&mut rig, 72); // < 0.75 * 1.967 s
    tick(&mut rig.app);
    step(&mut rig, 1);
    assert!(rig.app.world().resource::<PendingJacks>().0.is_empty());
    step(&mut rig, 2);
    let state = tick(&mut rig.app);
    // Production order: the snapshot's events become CoreEvents for read_jacks.
    let jacks: Vec<_> = state["events"].as_array().unwrap().iter().filter(|e| e["kind"] == "carjack").cloned().collect();
    assert_eq!(jacks.len(), 1, "server accepted exactly one jack");
    for e in &jacks {
        rig.app.world_mut().write_message(CoreEvent { sequence: e["seq"].as_u64().unwrap(), kind: "carjack".into(), data: e["data"].clone() });
    }
    step(&mut rig, 1);
    let pending = &rig.app.world().resource::<PendingJacks>().0;
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].0, car);
    assert!((480. ..=544.).contains(&pending[0].1.y));
    assert_eq!(count(&state, JACK), 1, "the jack is a tool, not consumed");
}

mod visual;
