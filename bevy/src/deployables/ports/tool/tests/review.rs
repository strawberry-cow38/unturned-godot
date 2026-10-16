//! Source-visible behavior found by independent review of the native tool.
use super::*;

fn bind(app: &mut App) {
    let mut settings = app.world_mut().resource_mut::<Settings>();
    settings.bindings.insert(GameAction::Fire, crate::settings::Binding::Key("KeyJ".into()));
    settings.bindings.insert(GameAction::Aim, crate::settings::Binding::Key("KeyK".into()));
}
fn click(app: &mut App, window: Entity) {
    key(app, window, KeyCode::KeyJ, true);
    key(app, window, KeyCode::KeyJ, false);
    step(app, 1);
}
fn wires(app: &App) -> usize {
    app.world().resource::<wires::Accepted>().graph.as_ref().unwrap().wires.len()
}

#[test]
fn held_clear_displays_the_source_info_line_that_overwrites_its_percentage() {
    let (mut app, window, consumer, source) = super::super::super::tests::fixture();
    bind(&mut app);
    for address in [source, consumer] {
        super::super::super::tests::aim(&mut app, address);
        click(&mut app, window);
    }
    super::super::super::tests::pump(&mut app);
    assert_eq!(wires(&app), 1);
    super::super::super::tests::aim(&mut app, source);
    key(&mut app, window, KeyCode::KeyK, true);
    step(&mut app, 8);
    let gesture = app.world().resource::<Gesture>();
    assert!(gesture.clear.is_some(), "hold is armed and timing");
    assert!(
        gesture.readout().starts_with("Generator — ")
            && gesture.readout().ends_with("   ([RMB] hold: clear · tap: unplug)"),
        "UpdateWireLook replaces the percentage in the same tick: {}",
        gesture.readout()
    );
    assert!(!gesture.readout().contains('%'));
}

#[test]
fn deactivated_start_keeps_its_route_until_the_device_despawns() {
    let (mut app, window, consumer, source) = super::super::super::tests::fixture();
    bind(&mut app);
    super::super::super::tests::aim(&mut app, consumer);
    click(&mut app, window);
    assert_eq!(app.world().resource::<Gesture>().start, Some(consumer));
    let set = |app: &mut App, f: &dyn Fn(&mut Vec<Value>)| {
        let mut snapshot = app.world_mut().resource_mut::<GameSnapshot>();
        f(snapshot.0["deployables"].as_array_mut().unwrap());
    };
    // Source KillPowerHardware deactivates the port cube; the ConnectionPort
    // node stays valid, so UpdateWireLook keeps routing.
    set(&mut app, &|rows| {
        for row in rows.iter_mut().filter(|r| r["netId"] == consumer.device) {
            row["health"] = json!(0);
        }
    });
    step(&mut app, 2);
    let frame = app.world().resource::<Frame>();
    assert!(!frame.endpoints[&consumer].usable, "retired start");
    assert!(frame.endpoints[&source].usable);
    assert_eq!(app.world().resource::<Gesture>().start, Some(consumer));
    assert!(
        tool::complete(frame.endpoints.get(&consumer), frame.endpoints.get(&source)),
        "CanCompleteWire checks only the target; the server refuses the retired source"
    );
    set(&mut app, &|rows| rows.retain(|r| r["netId"] != consumer.device));
    step(&mut app, 3);
    assert!(
        app.world().resource::<Gesture>().start.is_none(),
        "a freed start port drops the route"
    );
}
