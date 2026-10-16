//! Actual-UDP owner: the production placement systems against a remote peer of
//! a real listen host. The ghost is presentation; only the host spends.
use super::*;
use crate::{
    entities::InteractionFocus,
    ui::{Screen, UiState},
};
use bevy::{
    input::{
        keyboard::{Key, KeyboardInput},
        mouse::MouseButtonInput,
    },
    time::TimeUpdateStrategy,
};
use rules::{CANNOT_BUILD, NO_ROOM, TOO_FAR};
use std::path::Path;

#[derive(Resource, Default)]
pub(super) struct Collected(pub Vec<UiIntent>);
fn collect(mut reader: MessageReader<UiIntent>, mut out: ResMut<Collected>) {
    out.0.extend(reader.read().cloned());
}
pub(super) struct Udp {
    pub host: CoreBridge,
    pub app: App,
    pub window: Entity,
    pub camera: Entity,
    pub feet: Vec3,
}
pub(super) fn frames(app: &mut App, n: usize) {
    for _ in 0..n {
        app.update();
        app.world_mut().resource_mut::<FrameInput>().begin();
    }
}
impl Udp {
    pub fn pump(&mut self, n: usize) {
        let mut state = Value::Null;
        for _ in 0..n {
            state = self
                .app
                .world()
                .resource::<CoreBridge>()
                .request("tick", json!({}))
                .unwrap()["state"]
                .clone();
            self.host.request("tick", json!({})).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
        self.app
            .world_mut()
            .resource_mut::<UiModel>()
            .adopt_snapshot(&state);
        self.app.world_mut().resource_mut::<GameSnapshot>().0 = state;
        frames(&mut self.app, 2);
    }
    pub fn peer(&self, name: &str, args: Value) -> Value {
        self.app
            .world()
            .resource::<CoreBridge>()
            .request("action", json!({"name":name,"args":args}))
            .unwrap()
    }
    pub fn state(&self) -> &Value {
        &self.app.world().resource::<GameSnapshot>().0
    }
    pub fn cell(&self, id: u16) -> Cell {
        self.state()["inventory"]["pages"]
            .as_array()
            .unwrap()
            .iter()
            .find_map(|p| {
                p["items"]
                    .as_array()?
                    .iter()
                    .find(|i| i["id"] == id)
                    .map(|i| Cell {
                        id,
                        page: p["index"].as_u64().unwrap() as u8,
                        x: i["x"].as_u64().unwrap() as u8,
                        y: i["y"].as_u64().unwrap() as u8,
                    })
            })
            .unwrap_or_else(|| panic!("no {id} in the owner inventory"))
    }
    pub fn amount(&self, id: u16) -> u64 {
        self.state()["inventory"]["pages"]
            .as_array()
            .unwrap()
            .iter()
            .flat_map(|p| p["items"].as_array().into_iter().flatten())
            .filter(|i| i["id"] == id)
            .map(|i| i["amount"].as_u64().unwrap_or(1))
            .sum()
    }
    /// The inventory's equip-and-hold (a gun moves to its slot first).
    pub fn hold(&mut self, id: u16) -> Cell {
        let cell = self.cell(id);
        self.peer("equipAndHold", json!({"page":cell.page,"x":cell.x,"y":cell.y}));
        self.pump(15);
        let held = Cell::held(self.state()).unwrap_or_else(|| panic!("accepted hold of {id}"));
        assert_eq!(held.id, id);
        held
    }
    pub fn look(&mut self, at: Vec3) {
        let eye = self.feet + Vec3::Y * 1.6;
        *self.app.world_mut().get_mut::<Transform>(self.camera).unwrap() =
            Transform::from_translation(eye).looking_at(at, Vec3::Y);
        frames(&mut self.app, 2);
    }
    pub fn press(&mut self, button: MouseButton) {
        let window = self.window;
        let mut events = self.app.world_mut().resource_mut::<FrameInput>();
        for state in [ButtonState::Pressed, ButtonState::Released] {
            events.push(WindowEvent::MouseButtonInput(MouseButtonInput {
                button,
                state,
                window,
            }));
        }
    }
    pub fn key(&mut self, code: KeyCode, text: &str) {
        let window = self.window;
        let mut events = self.app.world_mut().resource_mut::<FrameInput>();
        for state in [ButtonState::Pressed, ButtonState::Released] {
            events.push(WindowEvent::KeyboardInput(KeyboardInput {
                key_code: code,
                logical_key: Key::Character(text.into()),
                state,
                text: None,
                repeat: false,
                window,
            }));
        }
    }
    pub fn placer(&self) -> &Placer {
        self.app.world().resource::<Placer>()
    }
    pub fn intents(&mut self) -> Vec<UiIntent> {
        std::mem::take(&mut self.app.world_mut().resource_mut::<Collected>().0)
    }
}
pub(super) fn launch(give: &[u16]) -> Udp {
    let port = std::net::UdpSocket::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"));
    let host = CoreBridge::launch(root).unwrap();
    host.request(
        "init",
        json!({"contentRoot":WorldConfig::default().content_root,
        "listenPort":port,"allowCheats":true,"spawn":[0,5,0]}),
    )
    .unwrap();
    let peer = CoreBridge::launch(root).unwrap();
    let init = peer
        .request(
            "init",
            json!({"contentRoot":WorldConfig::default().content_root,
            "connectHost":"127.0.0.1","connectPort":port}),
        )
        .unwrap();
    let visuals: Vec<crate::entities::ItemVisual> = serde_json::from_value(
        peer.request("catalog", json!({})).unwrap()["result"]["items"].clone(),
    )
    .unwrap();
    let mut app = super::super::tests::fixture(peer, init["state"].clone());
    app.world_mut().resource_mut::<ItemCatalog>().items =
        visuals.into_iter().map(|i| (i.id, i)).collect();
    app.insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .init_resource::<Settings>()
    .init_resource::<FrameInput>()
    .init_resource::<InteractionFocus>()
    .init_resource::<Collected>()
    .add_message::<UiIntent>()
    .add_message::<HandEvent>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(200),
    ))
    .add_plugins(RapierPhysicsPlugin::<NoUserData>::default())
    .add_systems(Update, collect.after(act));
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
    let camera = app
        .world_mut()
        .spawn((PlayerCamera, Transform::default()))
        .id();
    let mut udp = Udp {
        host,
        app,
        window,
        camera,
        feet: Vec3::ZERO,
    };
    udp.pump(40);
    for id in give {
        udp.peer("console", json!({"text":format!("give {id}")}));
        udp.pump(10);
    }
    udp.feet = crate::player::vector(&udp.state()["player"]["position"]).unwrap();
    // Source terrain under the owner: never an obstacle, always attachable.
    udp.app.world_mut().spawn((
        RigidBody::Fixed,
        Collider::cuboid(30., 0.5, 30.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_translation(udp.feet - Vec3::Y * 0.5),
        crate::world::WorldTerrain,
    ));
    frames(&mut udp.app, 2);
    udp
}

#[test]
fn udp_owner_ghost_places_only_through_the_accepted_spend_and_reverts_hands() {
    let mut u = launch(&[13, 458]);
    let prior = u.hold(13);
    frames(&mut u.app, 2);
    let jar = u.hold(458);
    let target = u.feet - Vec3::Z * 2.5;
    u.look(target);
    let placer = u.placer();
    assert_eq!(placer.def, Some(458));
    assert_eq!(placer.revert, Some(prior), "EquipHeldDeployable captures the prior hold");
    assert!(placer.active && placer.aim.valid, "{:?}", placer.aim);
    assert!(placer.aim.point.distance(target) < 0.01);
    let ghost = u.app.world().resource::<ghost::Ghost>().root.unwrap();
    assert_eq!(
        u.app.world().get::<Visibility>(ghost),
        Some(&Visibility::Inherited)
    );
    // The R spin is the source YawOffset added to the aim yaw.
    let before = u.placer().aim.yaw;
    u.key(KeyCode::KeyR, "r");
    frames(&mut u.app, 2);
    assert!((u.placer().aim.yaw - before - 90.).abs() < 1e-3);
    let pose = u.placer().aim;
    u.press(MouseButton::Left);
    frames(&mut u.app, 1);
    let gesture = u.placer().gesture.expect("a valid click freezes the gesture");
    assert_eq!((gesture.point, gesture.yaw), (pose.point, pose.yaw));
    // Looking away while the gesture plays leaves the frozen target.
    u.look(u.feet + Vec3::new(3., 0., 1.));
    frames(&mut u.app, 2);
    assert!(u.placer().gesture.is_none(), "0.45 s gesture completed");
    let sent = u.placer().sent.clone();
    assert_eq!(sent.len(), 1);
    assert_eq!(
        (&sent[0]["defId"], &sent[0]["page"], &sent[0]["x"], &sent[0]["y"]),
        (&json!(458), &json!(jar.page), &json!(jar.x), &json!(jar.y)),
        "Command 7 names the held jar"
    );
    assert!(
        u.app.world().resource::<Replicas>().instances.is_empty(),
        "no body before acceptance"
    );
    assert_eq!(u.amount(458), 1, "no local spend");
    let intents = u.intents();
    assert!(
        intents.iter().any(|i| matches!(i, UiIntent::Action { name, args }
            if name == "holdItem" && args == &json!({"page":prior.page,"x":prior.x,"y":prior.y}))),
        "the last jar reverts to the prior hold: {intents:?}"
    );
    u.pump(20);
    assert_eq!(u.amount(458), 0, "the host spent the named jar");
    let rows = u.state()["deployables"].as_array().unwrap().clone();
    assert_eq!(rows.len(), 1);
    let replicas = u.app.world().resource::<Replicas>();
    let instance = &replicas.instances[&(rows[0]["netId"].as_u64().unwrap() as u32)];
    let bounds = replicas.art[&458].bounds;
    let body = *u.app.world().get::<Transform>(instance.entity).unwrap();
    let def = u
        .app
        .world()
        .resource::<ItemCatalog>()
        .deployables
        .iter()
        .find(|d| d.id == 458)
        .unwrap()
        .clone();
    let ghost = rules::ghost_transform(&def, bounds, Mount::Floor, &pose);
    assert!(
        body.translation.distance(ghost.translation) < 0.02,
        "{body:?} vs {ghost:?}"
    );
    assert!(body.rotation.angle_between(ghost.rotation) < 0.01);
}

#[test]
fn red_ghost_names_the_source_reason_and_never_sends() {
    let mut u = launch(&[458]);
    u.hold(458);
    let ahead = u.feet - Vec3::Z * 2.5;
    let reason = |u: &Udp| (u.placer().aim.valid, u.placer().hint());
    u.look(u.feet + Vec3::new(0., 30., -1.));
    assert_eq!(reason(&u), (false, Some(TOO_FAR)));
    let wall = u
        .app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(2., 2., 0.1),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            Transform::from_translation(u.feet - Vec3::Z * 2. + Vec3::Y),
        ))
        .id();
    frames(&mut u.app, 2);
    u.look(u.feet + Vec3::new(0., 1.2, -2.));
    assert_eq!(reason(&u), (false, Some("Needs flat ground")));
    u.app.world_mut().despawn(wall);
    let block = u
        .app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(0.1, 0.1, 0.1),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
            Transform::from_translation(ahead + Vec3::new(0.3, 0.9, 0.)),
        ))
        .id();
    frames(&mut u.app, 2);
    u.look(ahead);
    assert_eq!(reason(&u), (false, Some(NO_ROOM)));
    u.press(MouseButton::Left);
    frames(&mut u.app, 4);
    assert!(
        u.placer().gesture.is_none() && u.placer().sent.is_empty(),
        "red never places"
    );
    u.app.world_mut().despawn(block);
    // Another deployable's surface: legal and clear, but not attachable.
    u.app.world_mut().spawn((
        Deployed {
            net_id: 9999,
            def_id: 458,
        },
        RigidBody::Fixed,
        Collider::cuboid(1., 0.2, 1.),
        CollisionGroups::new(Group::GROUP_1, Group::ALL),
        Transform::from_translation(ahead + Vec3::Y * 0.2),
    ));
    frames(&mut u.app, 2);
    u.look(ahead);
    assert_eq!(reason(&u), (false, Some(CANNOT_BUILD)));
    // The hint speaks only while the ghost answers the aim.
    u.app.world_mut().resource_mut::<UiState>().screen = Screen::Inventory;
    frames(&mut u.app, 2);
    assert!(!u.placer().active && u.placer().hint().is_none());
    u.app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    frames(&mut u.app, 2);
    u.intents();
    u.press(MouseButton::Right);
    frames(&mut u.app, 1);
    let intents = u.intents();
    assert!(
        intents.iter().any(|i| matches!(i, UiIntent::Action { name, args }
            if name == "selectHeld" && args["itemId"] == 0)),
        "RMB dequips: {intents:?}"
    );
    assert!(u.placer().sent.is_empty());
}
