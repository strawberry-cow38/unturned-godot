//! Source EquipHeldOptic/UpdateOptic/CycleOpticZoom and BinocularsView geometry.
use super::*;
use serde_json::json;

fn cell(id: u16, x: u8) -> Option<Cell> {
    Some(Cell { id, page: 2, x, y: 0 })
}

#[test]
fn a_fresh_pair_starts_lowered_at_4x_and_lmb_cycles_4_8_12_raised_or_not() {
    let mut b = Binoculars::default();
    assert!(!b.step(cell(333, 0), false, false));
    assert_eq!((b.zoom(), b.raised()), (4., false));
    assert!(b.step(cell(333, 0), false, true), "LMB steps the zoom and clicks");
    assert_eq!(b.zoom(), 8.);
    b.step(cell(333, 0), true, false);
    assert_eq!(b.raised_zoom(), Some(8.), "RMB held: at the eyes");
    assert_eq!(b.look_sensitivity(), 1. / 8., "the mouse turns 1/zoom as much");
    b.step(cell(333, 0), true, true);
    assert_eq!(b.zoom(), 12.);
    b.step(cell(333, 0), true, true);
    assert_eq!(b.zoom(), 4., "12x wraps to 4x");
    b.step(cell(333, 0), false, false);
    assert_eq!((b.raised_zoom(), b.look_sensitivity()), (None, 1.), "released: lowered, normal look");
    b.step(cell(333, 0), false, true);
    // Another equip (EquipHeldOptic) resets the level; nothing held lowers them.
    b.step(cell(333, 3), false, false);
    assert_eq!((b.zoom(), b.raised()), (4., false));
    b.step(None, true, true);
    assert!(!b.held() && !b.raised());
    let mut forced = Binoculars { force_raise: true, ..default() };
    forced.step(cell(333, 0), false, false);
    forced.step(cell(333, 0), false, false);
    assert!(forced.raised(), "UG_ADS=1 holds them up for a render");
}

#[test]
fn only_an_optic_hand_tool_in_a_living_hand_is_held() {
    let mut catalog = ItemCatalog::default();
    for (id, kind) in [(333, "optic"), (1195, "restraint")] {
        let item: crate::entities::ItemVisual = serde_json::from_value(json!({"id":id,"name":"x","type":"optic",
            "asset":null,"mesh":null,"texture":null,"icon":null,"handTool":{"kind":kind}})).unwrap();
        catalog.items.insert(id, item);
    }
    let snap = |id: u16, alive: bool| json!({"player":{"alive":alive},"weapon":{"held":{"id":id,"page":2,"x":1,"y":0}}});
    assert_eq!(held_optic(&snap(333, true), &catalog), cell(333, 1));
    assert_eq!(held_optic(&snap(333, false), &catalog), None);
    assert_eq!(held_optic(&snap(1195, true), &catalog), None);
}

#[test]
fn the_carry_follows_the_hook_basis_and_the_pip_looks_up_the_barrels() {
    let hook = GlobalTransform::from(
        Transform::from_xyz(0.2, -0.3, -0.5).with_rotation(Quat::from_rotation_y(0.4)),
    );
    let frame = GlobalTransform::from(Transform::from_xyz(5., 1.6, 2.).with_rotation(Quat::from_rotation_y(1.)));
    let carried = carried_pose(&hook, &frame);
    // Position: the hook plus HoldPos in view (frame) space.
    let expected = Vec3::new(0.2, -0.3, -0.5) + Quat::from_rotation_y(1.) * Vec3::new(0., 0.44, -0.14);
    assert!((Vec3::from(carried.translation) - expected).length() < 1e-5);
    // Basis: the hook's, then HoldRoll (0, 0, -90): local +X lands on the hook's -Y.
    let x = Vec3::from(carried.matrix3.x_axis);
    assert!((x - Quat::from_rotation_y(0.4) * Vec3::NEG_Y).length() < 1e-5, "{x}");
    let objective = objective_pose(carried);
    let along = Vec3::from(carried.matrix3.y_axis).normalize();
    assert!((objective.translation - (expected + along * OBJECTIVE_AHEAD)).length() < 1e-5);
    assert!((objective.forward().as_vec3() - along).length() < 1e-5, "the PiP camera looks along the barrels (+Y)");
}

mod visual;
