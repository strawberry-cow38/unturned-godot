use super::*;
use bevy::ecs::system::RunSystemOnce;
#[path = "mechanics_udp.rs"]
mod udp;
#[path = "mechanics_visual.rs"]
mod visual;
#[path = "maintenance_tests.rs"]
mod maintenance;

fn setup() -> App {
    let mut app = super::super::tests::setup();
    app.add_message::<MouseWheel>()
        .init_resource::<InteractionFocus>()
        .insert_resource(ItemCatalog::default())
        .add_observer(repair);
    let body = crate::vehicle::tests::body(&mut app, "police", Vec3::ZERO);
    app.world_mut().get_mut::<VehicleBody>(body).unwrap().net_id = 7;
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"][0] = json!({
        "netId":7,"typeId":data::named("police").unwrap().type_id,
        "flags":0,"health":4500,"fuel":2047.5,"battery":9990,
        "lampMask":3,"brokenLampMask":1,"poppedTireMask":0});
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .vehicle_lighting = Some(
        serde_json::from_value(json!({
            "lampLabels":["headlight_l","headlight_r"],"patternNames":[],"sirenPitch":[],
            "low":{"range":45,"energy":9,"angle":25,"attenuation":1.3},
            "high":{"range":98,"energy":17,"angle":21,"attenuation":0.85}
        }))
        .unwrap(),
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Mechanics;
    app.world_mut().resource_mut::<Menu>().mechanics = Some(7);
    app
}
fn render(app: &mut App) {
    app.world_mut().run_system_cached(build).unwrap();
    app.world_mut().run_system_cached(refresh).unwrap();
}
fn fixes(app: &mut App) -> Vec<(Entity, u8)> {
    let world = app.world_mut();
    let mut result: Vec<_> = world
        .query::<(Entity, &Fix)>()
        .iter(world)
        .filter(|(_, f)| f.1 == Part::Lamp)
        .map(|(e, f)| (e, f.2))
        .collect();
    result.sort_by_key(|x| x.1);
    result
}
#[test]
fn actual_panel_keeps_buttons_through_damage_changes_and_only_requests_broken_lamp() {
    let mut app = setup();
    render(&mut app);
    let buttons = fixes(&mut app);
    assert_eq!(buttons.len(), 2);
    let world = app.world_mut();
    for (caption, color) in world.query::<(&FixText, &TextColor)>().iter(world) {
        assert_eq!(color.0.alpha(), if caption.1 == 0 { 1. } else { 0.5 });
    }
    for layout in world
        .query_filtered::<&TextLayout, With<PartText>>()
        .iter(world)
    {
        assert_eq!(layout.linebreak, LineBreak::NoWrap);
    }
    assert!(
        !app.world()
            .entity(buttons[0].0)
            .contains::<bevy::ui::InteractionDisabled>()
    );
    assert!(
        app.world()
            .entity(buttons[1].0)
            .contains::<bevy::ui::InteractionDisabled>()
    );
    app.world_mut().trigger(Activate {
        entity: buttons[1].0,
    });
    assert!(super::super::tests::events(&mut app).is_empty());
    app.world_mut().trigger(Activate {
        entity: buttons[0].0,
    });
    assert!(matches!(&super::super::tests::events(&mut app)[..],
        [UiIntent::Action{name,args}] if name=="vehicleLighting"&&args["netId"]==7&&args["action"]=="RepairLamp"&&args["value"]==0));
    assert_eq!(
        row(&app.world().resource::<GameSnapshot>().0, 7).unwrap()["brokenLampMask"],
        1,
        "clicking must not repair the local accepted view"
    );
    app.world_mut().resource_mut::<GameSnapshot>().0["vehicles"][0]["brokenLampMask"] = json!(2);
    render(&mut app);
    assert_eq!(
        fixes(&mut app),
        buttons,
        "do not replace a pressed button before release"
    );
    assert!(
        app.world()
            .entity(buttons[0].0)
            .contains::<bevy::ui::InteractionDisabled>()
    );
    assert!(
        !app.world()
            .entity(buttons[1].0)
            .contains::<bevy::ui::InteractionDisabled>()
    );
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().trigger(Activate {
        entity: buttons[1].0,
    });
    assert!(
        super::super::tests::events(&mut app).is_empty(),
        "closed panels cannot issue stale repairs"
    );
    render(&mut app);
    assert!(fixes(&mut app).is_empty());
}

#[test]
fn source_hood_focus_competes_for_the_nearest_target_and_rebound_close_consumes_only_its_edge() {
    let mut app = setup();
    use bevy_rapier3d::prelude::*;
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        RapierPhysicsPlugin::<NoUserData>::default(),
    ));
    let w = app.world_mut();
    let bodies: Vec<_> = w
        .query_filtered::<Entity, With<VehicleBody>>()
        .iter(w)
        .collect();
    for entity in bodies {
        // The fixture's coarse chassis box is not original visible geometry.
        w.entity_mut(entity)
            .remove::<Collider>()
            .insert(RigidBody::Fixed);
    }
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut().resource_mut::<GameSnapshot>().0["player"]["vehicleNetId"] = json!(0);
    let def = data::named("police").unwrap();
    let hood = def.access.iter().find(|z| z.kind == "Hood").unwrap();
    let center = Vec3::from_array(hood.center);
    let size = Vec3::from_array(hood.size);
    app.world_mut().spawn((
        PlayerCamera,
        Transform::from_translation(center - Vec3::Z * (size.z * 0.5 + 1.))
            .looking_at(center, Vec3::Y),
    ));
    app.update();
    app.update();
    app.world_mut().run_system_once(focus).unwrap();
    assert!(
        app.world()
            .resource::<InteractionFocus>()
            .selected(ACTION, 7)
    );
    let wall = app
        .world_mut()
        .spawn((
            RigidBody::Fixed,
            Collider::cuboid(2., 2., 0.05),
            Transform::from_translation(center - Vec3::Z * (size.z * 0.5 + 0.5)),
            CollisionGroups::new(Group::GROUP_1, Group::ALL),
        ))
        .id();
    app.update();
    app.update();
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut().run_system_once(focus).unwrap();
    assert!(
        !app.world().resource::<InteractionFocus>().has_candidate(),
        "an ordinary wall clips the source look segment before the hood"
    );
    app.world_mut().despawn(wall);
    app.update();
    app.update();
    app.world_mut().run_system_once(focus).unwrap();
    assert!(
        app.world()
            .resource::<InteractionFocus>()
            .selected(ACTION, 7)
    );
    app.world_mut()
        .resource_mut::<InteractionFocus>()
        .offer_manual(0.01, "Other", "other", json!({"netId":8}));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyF);
    app.world_mut().run_system_once(open).unwrap();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    *app.world_mut().resource_mut::<InteractionFocus>() = default();
    app.world_mut().run_system_once(focus).unwrap();
    app.world_mut().run_system_once(open).unwrap();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Mechanics);
    app.world_mut()
        .resource_mut::<Settings>()
        .bindings
        .insert(GameAction::Interact, Binding::Key("KeyG".into()));
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyG);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyW);
    app.world_mut()
        .run_system_once(super::super::input)
        .unwrap();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    let keys = app.world().resource::<ButtonInput<KeyCode>>();
    assert!(!keys.just_pressed(KeyCode::KeyG));
    assert!(keys.pressed(KeyCode::KeyW));
    app.world_mut().run_system_once(open).unwrap();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
}
