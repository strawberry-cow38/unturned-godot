use super::*;
mod place_ray;
mod source;
mod visual;
use crate::{
    settings::Settings,
    ui::{Screen, UiState, input_order::FrameInput},
};
use bevy::{
    input::{ButtonState, mouse::MouseButtonInput},
    time::TimeUpdateStrategy,
    window::{CursorGrabMode, CursorOptions, PrimaryWindow, WindowEvent},
};

pub(super) fn step(app: &mut App, n: usize) {
    for _ in 0..n {
        app.update();
        app.world_mut().resource_mut::<FrameInput>().begin();
    }
}
pub(super) fn pump(app: &mut App) {
    let state = super::super::tests::tick(app.world().resource::<CoreBridge>());
    app.world_mut()
        .resource_mut::<UiModel>()
        .adopt_snapshot(&state);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state;
    step(app, 2);
}
fn edge(app: &mut App, window: Entity, button: MouseButton, down: bool) {
    app.world_mut()
        .resource_mut::<FrameInput>()
        .push(WindowEvent::MouseButtonInput(MouseButtonInput {
            button,
            state: if down {
                ButtonState::Pressed
            } else {
                ButtonState::Released
            },
            window,
        }));
}
pub(super) fn aim(app: &mut App, address: Address) {
    let point = app.world().resource::<Frame>().endpoints[&address].position;
    let mut q = app
        .world_mut()
        .query_filtered::<&mut Transform, With<PlayerCamera>>();
    *q.single_mut(app.world_mut()).unwrap() =
        Transform::from_translation(point + Vec3::Z * 2.).looking_at(point, Vec3::Y);
    step(app, 2);
    assert_eq!(app.world().resource::<Frame>().hovered, Some(address));
}

pub(super) fn fixture() -> (App, Entity, Address, Address) {
    let bridge = super::super::tests::initialize();
    for id in [458, 459, 65] {
        bridge
            .request(
                "action",
                json!({"name":"console","args":{"text":format!("give {id}")}}),
            )
            .unwrap();
        super::super::tests::tick(&bridge);
    }
    for (id, x) in [(458, -1.), (459, 1.)] {
        bridge
            .request(
                "action",
                json!({"name":"placeDeployable","args":{"defId":id,"pos":[x,5,-2],"yawDegrees":0}}),
            )
            .unwrap();
        super::super::tests::tick(&bridge);
    }
    let mut state = super::super::tests::tick(&bridge);
    let cell = state["inventory"]["pages"]
        .as_array()
        .unwrap()
        .iter()
        .find_map(|p| {
            p["items"]
                .as_array()
                .unwrap()
                .iter()
                .find(|i| i["id"] == 65)
                .map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone()))
        })
        .unwrap();
    bridge
        .request(
            "action",
            json!({"name":"holdItem","args":{"page":cell.0,"x":cell.1,"y":cell.2}}),
        )
        .unwrap();
    state = super::super::tests::tick(&bridge);
    let rows = state["deployables"].as_array().unwrap();
    let id = |def| {
        rows.iter().find(|r| r["defId"] == def).unwrap()["netId"]
            .as_u64()
            .unwrap() as u32
    };
    let (gen_id, lamp_id) = (id(458), id(459));
    let visuals: Vec<crate::entities::ItemVisual> = serde_json::from_value(
        bridge.request("catalog", json!({})).unwrap()["result"]["items"].clone(),
    )
    .unwrap();
    let mut app = super::super::tests::fixture(bridge, state);
    app.world_mut().resource_mut::<ItemCatalog>().items =
        visuals.into_iter().map(|i| (i.id, i)).collect();
    app.insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(50),
    ))
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default());
    let window = app
        .world_mut()
        .spawn((
            PrimaryWindow,
            Window::default(),
            CursorOptions {
                visible: false,
                grab_mode: CursorGrabMode::Locked,
                ..default()
            },
        ))
        .id();
    app.world_mut().spawn((PlayerCamera, Transform::default()));
    step(&mut app, 4);
    let start = Address {
        device: lamp_id,
        index: 0,
    };
    let target = Address {
        device: gen_id,
        index: 0,
    };
    (app, window, start, target)
}

#[test]
fn original_port_only_aim_routes_both_directions_and_waits_for_accepted_graph_to_connect_or_unplug()
{
    let (mut app, window, start, target) = fixture();
    let (gen_id, lamp_id) = (target.device, start.device);
    aim(&mut app, start);
    let point = app.world().resource::<Frame>().endpoints[&start].position;
    let blocker = app
        .world_mut()
        .spawn((
            Collider::cuboid(0.5, 0.5, 0.05),
            Transform::from_translation(point + Vec3::Z),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    step(&mut app, 2);
    assert_eq!(
        app.world().resource::<Frame>().hovered,
        Some(start),
        "source port-only aim ignores ordinary occlusion"
    );
    edge(&mut app, window, MouseButton::Left, true);
    edge(&mut app, window, MouseButton::Left, false);
    step(&mut app, 1);
    assert_eq!(
        app.world().resource::<tool::Gesture>().start,
        Some(start),
        "consumer can start routing"
    );
    aim(&mut app, target);
    edge(&mut app, window, MouseButton::Left, true);
    edge(&mut app, window, MouseButton::Left, false);
    step(&mut app, 1);
    assert!(app.world().resource::<tool::Gesture>().start.is_none());
    assert!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .is_empty(),
        "completion submits without a local edge"
    );
    pump(&mut app);
    let graph = app
        .world()
        .resource::<wires::Accepted>()
        .graph
        .as_ref()
        .unwrap();
    assert_eq!(graph.wires.len(), 1);
    assert_eq!(
        (graph.wires[0].source_id, graph.wires[0].consumer_id),
        (gen_id, lamp_id),
        "reverse route orders graph endpoints"
    );
    aim(&mut app, start);
    edge(&mut app, window, MouseButton::Right, true);
    edge(&mut app, window, MouseButton::Right, false);
    step(&mut app, 1);
    assert_eq!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .len(),
        1,
        "tap unplug waits for accepted removal"
    );
    pump(&mut app);
    assert!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .is_empty()
    );
    assert!(
        app.world().resource::<tool::Gesture>().start.is_none(),
        "unified server wire does not rehydrate routed SP nodes"
    );
    app.world_mut().despawn(blocker);
}
