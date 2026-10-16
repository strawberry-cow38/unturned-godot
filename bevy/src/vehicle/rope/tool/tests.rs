//! The native rope tool against a real local authority: injected ordered
//! edges, accepted tie/untie only, source HUD strings and timing.
use super::*;
use crate::{
    entities::ItemVisual,
    player::WorldSessionEpoch,
    ui::{Screen, UiState},
    world::WorldConfig,
};
use bevy::{ecs::system::RunSystemOnce, input::mouse::MouseButtonInput};
use serde_json::Value;
use std::path::Path;

const SPAN: f32 = 5.956; // sedan rear-to-front tow node span along Z
struct Rig {
    app: App,
    window: Entity,
    tower: u32,
    towed: u32,
}
fn tick(app: &mut App) -> Value {
    let mut state = Value::Null;
    for _ in 0..12 {
        state = app.world().resource::<CoreBridge>().request("tick", json!({})).unwrap()["state"].clone();
    }
    app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
    // The production adoption of the accepted rope onto the native bodies.
    app.world_mut().run_system_once(crate::vehicle::sync_vehicles).unwrap();
    state
}
fn rig() -> Rig {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge.request("init", json!({"contentRoot":WorldConfig::default().content_root,"name":"rope-tool","spawn":[3,0,4],"allowCheats":true})).unwrap();
    let sedan = data::named("sedan").unwrap();
    let fleet = bridge.request("worldVehicles", json!({"vehicles":[
        registration(sedan, "rope tower".into(), Vec3::ZERO, 0., 0),
        registration(sedan, "rope towed".into(), Vec3::Z * (SPAN + 3.), 0., 0)]})).unwrap();
    let id = |i: usize| fleet["result"]["vehicles"][i]["netId"].as_u64().unwrap() as u32;
    let (tower, towed) = (id(0), id(1));
    bridge.request("action", json!({"name":"console","args":{"text":"give 64"}})).unwrap();
    let mut state = Value::Null;
    for _ in 0..12 { state = bridge.request("tick", json!({})).unwrap()["state"].clone(); }
    let cell = state["inventory"]["pages"].as_array().unwrap().iter().find_map(|p| {
        p["items"].as_array().unwrap().iter().find(|i| i["id"] == 64).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
    }).unwrap();
    bridge.request("action", json!({"name":"holdItem","args":{"page":cell.0,"x":cell.1,"y":cell.2}})).unwrap();
    let visuals: Vec<ItemVisual> = serde_json::from_value(bridge.request("catalog", json!({})).unwrap()["result"]["items"].clone()).unwrap();
    let mut catalog = ItemCatalog::default();
    catalog.items = visuals.into_iter().map(|i| (i.id, i)).collect();
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), TransformPlugin))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .insert_resource(bridge)
        .insert_resource(catalog)
        .insert_resource(GameSnapshot(Value::Null))
        .init_resource::<UiModel>()
        .insert_resource(UiState { screen: Screen::Playing, ..default() })
        .init_resource::<Settings>()
        .init_resource::<FrameInput>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<ToolReadouts>()
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(std::time::Duration::from_millis(20)))
        .add_systems(Update, (nubs, input, present).chain())
        .init_resource::<RopeTool>()
        .init_resource::<Preview>()
        .init_asset::<Image>()
        .insert_resource(WorldConfig::default())
        .init_resource::<crate::content::ContentCache>()
        .init_resource::<Vehicles>()
        .add_message::<crate::ui::UiIntent>();
    let window = app.world_mut().spawn((PrimaryWindow, Window { focused: true, ..default() },
        CursorOptions { visible: false, grab_mode: CursorGrabMode::Locked, ..default() })).id();
    app.world_mut().spawn((PlayerCamera, Transform::default()));
    for (id, z) in [(tower, 0.), (towed, SPAN + 3.)] {
        let e = super::super::super::tests::body(&mut app, "sedan", Vec3::Z * z);
        let mut b = app.world_mut().get_mut::<VehicleBody>(e).unwrap();
        b.net_id = id;
        b.driver_player_id = 0;
        app.world_mut().resource_mut::<Vehicles>().entities.insert(id, e);
    }
    let mut rig = Rig { app, window, tower, towed };
    tick(&mut rig.app);
    rig
}
fn aim(rig: &mut Rig, id: u32, rear: bool) {
    let spec = &data::named("sedan").unwrap().spec;
    let z = if id == rig.tower { 0. } else { SPAN + 3. };
    let node = Vec3::Z * z + tow_local(spec, rear);
    *rig.app.world_mut().query_filtered::<&mut Transform, With<PlayerCamera>>().single_mut(rig.app.world_mut()).unwrap() =
        Transform::from_translation(node + Vec3::new(2.5, 1., 0.)).looking_at(node, Vec3::Y);
    step(rig, 1);
}
fn step(rig: &mut Rig, n: usize) {
    for _ in 0..n {
        rig.app.update();
        rig.app.world_mut().resource_mut::<FrameInput>().begin();
    }
}
fn mouse(rig: &mut Rig, button: MouseButton, down: bool) {
    let window = rig.window;
    rig.app.world_mut().resource_mut::<FrameInput>().push(WindowEvent::MouseButtonInput(MouseButtonInput {
        button,
        state: if down { ButtonState::Pressed } else { ButtonState::Released },
        window,
    }));
}
fn hud(rig: &Rig) -> String {
    rig.app.world().resource::<RopeTool>().hud.clone()
}
fn towed_by_server(rig: &mut Rig) -> u64 {
    let tower = rig.tower;
    let state = tick(&mut rig.app);
    state["vehicles"].as_array().unwrap().iter().find(|r| r["netId"] == tower).unwrap()["towedNetId"].as_u64().unwrap()
}

#[test]
fn rope_tool_ties_rear_to_front_and_waits_for_the_accepted_relationship() {
    let mut rig = rig();
    let (tower, towed) = (rig.tower, rig.towed);
    aim(&mut rig, tower, true);
    assert_eq!(rig.app.world().resource::<RopeTool>().looked, Some((tower, true)));
    assert_eq!(hud(&rig), "[LMB] start tow");
    aim(&mut rig, towed, false);
    mouse(&mut rig, MouseButton::Left, true);
    mouse(&mut rig, MouseButton::Left, false);
    step(&mut rig, 1);
    assert!(rig.app.world().resource::<RopeTool>().start.is_none(), "a front node cannot start a tow");
    aim(&mut rig, tower, true);
    mouse(&mut rig, MouseButton::Left, true);
    mouse(&mut rig, MouseButton::Left, false);
    step(&mut rig, 1);
    assert_eq!(rig.app.world().resource::<RopeTool>().start, Some(tower));
    // PlayerController shows the pending length whether or not input is captured.
    rig.app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    step(&mut rig, 1);
    assert!(hud(&rig).starts_with("tow rope   "), "{}", hud(&rig));
    rig.app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    aim(&mut rig, towed, false);
    assert_eq!(hud(&rig), "tow rope   3.0/4.5m   [LMB] tie");
    mouse(&mut rig, MouseButton::Left, true);
    mouse(&mut rig, MouseButton::Left, false);
    step(&mut rig, 1);
    assert!(rig.app.world().resource::<RopeTool>().start.is_none());
    let roped = rig.app.world_mut().query::<&VehicleBody>().iter(rig.app.world()).filter(|b| b.rope.roped()).count();
    assert_eq!(
        roped,
        0,
        "the tie is an intent: nothing is roped before the authority accepts it"
    );
    assert_eq!(towed_by_server(&mut rig), towed as u64);
    let body = |rig: &mut Rig, id: u32| {
        let mut q = rig.app.world_mut().query::<&VehicleBody>();
        let b = q.iter(rig.app.world()).find(|b| b.net_id == id).unwrap();
        (b.rope, b.park.parked)
    };
    let ((tower_rope, tower_parked), (towed_rope, towed_parked)) = (body(&mut rig, tower), body(&mut rig, towed));
    assert_eq!((tower_rope.towed, towed_rope.by), (towed, tower), "sync_vehicles adopts both rope ends");
    // Exact 2.997 m gap; the wire copy would floor it to 2.9375.
    assert!((tower_rope.rest - 3.).abs() < 0.01, "the host's exact rest: {}", tower_rope.rest);
    assert!(!tower_parked && !towed_parked, "AttachTow Wake()s both ends");
    aim(&mut rig, tower, true);
    assert_eq!(hud(&rig), "[RMB]  hold: clear rope  ·  tap: disconnect");
    let nubs: Vec<_> = rig.app.world_mut().query::<(&TowNub, &ChildOf, &Visibility)>().iter(rig.app.world())
        .map(|(n, p, v)| (n.rear, p.parent(), *v)).collect();
    assert_eq!(nubs.iter().filter(|n| n.2 != Visibility::Hidden).count(), 2,
        "SetTowNodesVisible hides the towing car's front and the towed car's rear");
}

#[test]
fn rmb_tap_disconnects_and_a_full_hold_clears_with_the_visible_source_percentage() {
    let mut rig = rig();
    let (tower, towed) = (rig.tower, rig.towed);
    let tie = |rig: &mut Rig| {
        rig.app.world().resource::<CoreBridge>().request("action", json!({"name":"attachTow","args":{"towerNetId":tower,"towedNetId":towed}})).unwrap();
        assert_eq!(towed_by_server(rig), towed as u64);
    };
    tie(&mut rig);
    aim(&mut rig, towed, false);
    mouse(&mut rig, MouseButton::Right, true);
    step(&mut rig, 1);
    mouse(&mut rig, MouseButton::Right, false);
    step(&mut rig, 1);
    assert_eq!(towed_by_server(&mut rig), 0, "a tap disconnects from either end");
    tie(&mut rig);
    aim(&mut rig, tower, true);
    mouse(&mut rig, MouseButton::Right, true);
    step(&mut rig, 25);
    assert!(hud(&rig).starts_with("clearing tow rope... ") && hud(&rig).ends_with('%'), "{}", hud(&rig));
    assert_eq!(towed_by_server(&mut rig), towed as u64, "mid-hold is not yet a clear");
    step(&mut rig, 30);
    assert_eq!(towed_by_server(&mut rig), 0, "a full one-second hold clears the rope");
}

mod visual;
