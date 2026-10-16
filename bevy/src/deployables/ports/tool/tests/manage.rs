use super::*;

#[test]
fn routing_cancel_cannot_arm_existing_wire_and_manage_midrelease_lookaway_and_hold_follow_source() {
    let (mut app, window, consumer, source) = super::super::super::tests::fixture();
    app.world_mut().resource_mut::<Settings>().bindings.insert(
        GameAction::Fire,
        crate::settings::Binding::Key("KeyJ".into()),
    );
    app.world_mut().resource_mut::<Settings>().bindings.insert(
        GameAction::Aim,
        crate::settings::Binding::Key("KeyK".into()),
    );
    for address in [source, consumer] {
        super::super::super::tests::aim(&mut app, address);
        key(&mut app, window, KeyCode::KeyJ, true);
        key(&mut app, window, KeyCode::KeyJ, false);
        step(&mut app, 1);
    }
    super::super::super::tests::pump(&mut app);
    assert_eq!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .len(),
        1
    );
    let spare = Address {
        device: consumer.device,
        index: 1,
    };
    super::super::super::tests::aim(&mut app, spare);
    key(&mut app, window, KeyCode::KeyJ, true);
    key(&mut app, window, KeyCode::KeyJ, false);
    step(&mut app, 1);
    assert_eq!(app.world().resource::<Gesture>().start, Some(spare));
    super::super::super::tests::aim(&mut app, source);
    key(&mut app, window, KeyCode::KeyK, true);
    step(&mut app, 25);
    assert!(app.world().resource::<Gesture>().start.is_none());
    assert!(
        app.world().resource::<Gesture>().clear.is_none(),
        "routing-cancel press never manages another completed wire"
    );
    key(&mut app, window, KeyCode::KeyK, false);
    step(&mut app, 1);
    super::super::super::tests::pump(&mut app);
    assert_eq!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .len(),
        1
    );
    key(&mut app, window, KeyCode::KeyK, true);
    step(&mut app, 9);
    key(&mut app, window, KeyCode::KeyK, false);
    step(&mut app, 1);
    super::super::super::tests::pump(&mut app);
    assert_eq!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .len(),
        1,
        "mid-hold release .28<time<1 aborts without unplug"
    );
    key(&mut app, window, KeyCode::KeyK, true);
    step(&mut app, 1);
    super::super::super::tests::aim(&mut app, consumer);
    step(&mut app, 25);
    key(&mut app, window, KeyCode::KeyK, false);
    step(&mut app, 1);
    super::super::super::tests::pump(&mut app);
    assert_eq!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .len(),
        1,
        "looking at other endpoint cancels armed hold"
    );
    key(&mut app, window, KeyCode::KeyK, true);
    step(&mut app, 21);
    assert!(
        app.world().resource::<Gesture>().clear.is_none(),
        "one full hold submits exactly once"
    );
    assert_eq!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .len(),
        1,
        "full clear waits for acceptance"
    );
    step(&mut app, 10);
    super::super::super::tests::pump(&mut app);
    assert!(
        app.world()
            .resource::<wires::Accepted>()
            .graph
            .as_ref()
            .unwrap()
            .wires
            .is_empty()
    );
}
