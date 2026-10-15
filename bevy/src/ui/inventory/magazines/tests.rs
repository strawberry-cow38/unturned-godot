use super::*;

fn view() -> api::View {
    serde_json::from_value(json!({"items":[
        {"address":{"page":2,"x":0,"y":0,"id":6,"token":10},"magazine":true,"magazineCapacity":30,"roundId":901},
        {"address":{"page":2,"x":5,"y":0,"id":901,"token":11}},
        {"address":{"page":3,"x":0,"y":0,"id":901,"token":12}}],
        "tiles":{"cells":[{"page":2,"x":0,"y":0,"tile":{"amount":0}},
        {"page":2,"x":5,"y":0,"tile":{"amount":1}},
        {"page":3,"x":0,"y":0,"tile":{"amount":2}}]}})).unwrap()
}
fn request(value: &UiIntent) -> &serde_json::Value {
    let UiIntent::Action { name, args } = value else {
        panic!("expected authority request")
    };
    assert_eq!(name, "magazineTransfer");
    args
}
fn load(ops: &mut Magazines, view: &api::View) {
    assert!(ops.load(
        &view.items[0],
        &api::Load {
            allowed: true,
            batch: 3,
            round_id: 901,
            ..Default::default()
        }
    ));
}
#[test]
fn timer_pauses_on_close_and_waits_for_both_exact_result_and_echo_without_editing_the_bag() {
    let mut view = view();
    let before = view.clone();
    let mut ops = Magazines::default();
    load(&mut ops, &view);
    assert!(ops.step(true, 0.49, &view).is_empty());
    assert!(ops.step(false, 20., &view).is_empty());
    let sent = ops.step(true, 0.02, &view);
    assert_eq!(sent.len(), 1);
    assert_eq!(request(&sent[0])["requestId"], 1);
    assert_eq!(request(&sent[0])["roundToken"], 11);
    assert!(ops.step(true, 2., &view).is_empty());
    assert_eq!(view, before);
    let fact = Completion {
        request_id: 1,
        magazine_token: 10,
        accepted_tick: 5,
        applied: true,
        amount: 1,
    };
    assert_eq!(ops.receive(&fact, &view), Some(30));
    assert_eq!(ops.receive(&fact, &view), None);
    assert!((ops.operations[0].progress() - 1. / 3.).abs() < 1e-6);
    assert!(
        ops.step(true, 1., &view).is_empty(),
        "accepted event arrived before inventory echo"
    );
    view.accepted_tick = 5;
    view.tiles.cells[0].tile.amount = 1;
    view.tiles.cells[1].tile.amount = 0;
    let sent = ops.step(true, 0., &view);
    assert_eq!(request(&sent[0])["requestId"], 2);
    assert_eq!(
        request(&sent[0])["roundToken"],
        12,
        "original first positive stack in page order"
    );
    assert_eq!(
        view.tiles.cells[0].tile.amount, 1,
        "request does not optimistically load"
    );
}
#[test]
fn refusal_ends_job_reopen_keeps_counter_and_replacement_never_receives_old_operation() {
    let mut view = view();
    view.tiles.cells[0].tile.amount = 3;
    let mut ops = Magazines::default();
    assert!(ops.unload(&view, Cell::at(2, 0, 0)));
    let sent = ops.step(true, 0.5, &view);
    let args = request(&sent[0]);
    assert_eq!(args["unloading"], true);
    assert_eq!(args["roundToken"], 0);
    assert_eq!(args["roundPage"], 0);
    assert_eq!(
        ops.receive(
            &Completion {
                request_id: 1,
                magazine_token: 10,
                accepted_tick: 5,
                applied: false,
                amount: 0
            },
            &view
        ),
        None
    );
    assert!(ops.operations.is_empty());
    assert_eq!(view.tiles.cells[0].tile.amount, 3);
    assert!(ops.step(false, 100., &view).is_empty());
    assert!(ops.unload(&view, Cell::at(2, 0, 0)));
    assert_eq!(request(&ops.step(true, 0.5, &view)[0])["requestId"], 2);
    view.items[0].address.token = 99;
    assert!(ops.step(true, 1., &view).is_empty());
    assert!(ops.operations.is_empty());
    ops.reset_session();
    assert!(ops.unload(&view, Cell::at(2, 0, 0)));
    assert_eq!(request(&ops.step(true, 0.5, &view)[0])["requestId"], 1);
}
