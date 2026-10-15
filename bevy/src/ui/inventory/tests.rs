use super::super::{InventoryItem, InventoryPage};
use super::{gesture::*, layout::*, model::*, transfer::*};
use bevy::prelude::*;

fn item(id: u16, x: u8, y: u8, amount: u16) -> InventoryItem {
    InventoryItem {
        id,
        x,
        y,
        amount,
        width: 2,
        height: 1,
        ..default()
    }
}
fn pages() -> Vec<InventoryPage> {
    vec![
        InventoryPage {
            index: 2,
            width: 6,
            height: 4,
            items: vec![item(146, 1, 1, 1), item(44, 3, 2, 12)],
        },
        InventoryPage {
            index: 7,
            width: 6,
            height: 4,
            ..default()
        },
    ]
}
fn layout() -> HitLayout {
    let mut layout = HitLayout {
        viewport: Vec2::new(1920., 1080.),
        ..default()
    };
    for (page, x) in [(2, 640.0), (7, 1280.0)] {
        let rect = Rect::from_corners(
            Vec2::new(x, 200.),
            Vec2::new(x + CELL * 6., 200. + CELL * 4.),
        );
        layout.grids.push(Grid {
            page,
            rect,
            clip: rect,
            slot: false,
        });
    }
    layout.paperdoll = Some(Rect::from_corners(
        Vec2::new(12., 150.),
        Vec2::new(628., 550.),
    ));
    layout
}
fn position(layout: &HitLayout, page: u8, x: u8, y: u8) -> Vec2 {
    layout
        .grids
        .iter()
        .find(|g| g.page == page)
        .unwrap()
        .rect
        .min
        + Vec2::new(x.into(), y.into()) * CELL
        + Vec2::splat(8.)
}
fn input(g: &mut Gesture, e: Edge, p: Vec2, l: &HitLayout, pages: &[InventoryPage]) -> Vec<Intent> {
    g.input(e, p, l, pages, &Default::default(), |_| {
        ItemRules::default()
    })
}

#[test]
fn original_grab_offset_drags_detached_optic_to_exact_cell_and_never_mutates_replica() {
    let pages = pages();
    let original = pages.clone();
    let l = layout();
    let mut g = Gesture::default();
    let from = position(&l, 2, 1, 1) + Vec2::new(60., 20.);
    input(
        &mut g,
        Edge::Left {
            down: true,
            control: false,
        },
        from,
        &l,
        &pages,
    );
    for expected in [1, 2, 3, 0] {
        input(&mut g, Edge::Rotate, from, &l, &pages);
        assert_eq!(g.carry.as_ref().unwrap().rotation, expected);
    }
    let drop = position(&l, 7, 2, 1) + Vec2::new(60., 20.);
    assert_eq!(
        input(
            &mut g,
            Edge::Left {
                down: false,
                control: false
            },
            drop,
            &l,
            &pages
        ),
        vec![Intent::LoadOrMove {
            from: Cell::at(2, 1, 1),
            to: Cell::at(7, 2, 1),
            rotation: 0
        }]
    );
    assert_eq!(pages, original);
}

#[test]
fn right_click_stack_arms_context_or_one_item_carry_only_after_six_pixels() {
    let pages = pages();
    let l = layout();
    let mut g = Gesture::default();
    let p = position(&l, 2, 3, 2);
    input(
        &mut g,
        Edge::Right {
            down: true,
            control: false,
        },
        p,
        &l,
        &pages,
    );
    input(
        &mut g,
        Edge::Motion {
            delta: Vec2::X * 6.,
        },
        p + Vec2::X * 6.,
        &l,
        &pages,
    );
    assert!(g.carry.is_none());
    assert!(g.selected.is_none());
    input(
        &mut g,
        Edge::Right {
            down: false,
            control: false,
        },
        p + Vec2::X * 6.,
        &l,
        &pages,
    );
    assert_eq!(g.selected, Some(Cell::at(2, 3, 2)));
    input(
        &mut g,
        Edge::Right {
            down: true,
            control: false,
        },
        p,
        &l,
        &pages,
    );
    input(
        &mut g,
        Edge::Motion {
            delta: Vec2::X * 7.,
        },
        p + Vec2::X * 7.,
        &l,
        &pages,
    );
    assert_eq!(g.carry.as_ref().unwrap().item.amount, 1);
    assert!(g.selected.is_none());
    assert_eq!(
        input(
            &mut g,
            Edge::Right {
                down: false,
                control: false
            },
            position(&l, 7, 1, 2),
            &l,
            &pages
        ),
        vec![Intent::Split {
            from: Cell::at(2, 3, 2),
            amount: 1,
            to: Cell::at(7, 1, 2),
            rotation: 0
        }]
    );
    assert_eq!(at(&pages, Cell::at(2, 3, 2)).unwrap().amount, 12);
}

#[test]
fn right_cancel_split_abandon_and_paperdoll_take_never_fall_through_to_world_drop() {
    let mut pages = pages();
    pages[1].items.push(item(44, 0, 0, 12));
    let l = layout();
    let mut g = Gesture::default();
    let p = position(&l, 2, 1, 1);
    input(
        &mut g,
        Edge::Left {
            down: true,
            control: false,
        },
        p,
        &l,
        &pages,
    );
    assert!(
        input(
            &mut g,
            Edge::Right {
                down: true,
                control: false
            },
            p,
            &l,
            &pages
        )
        .is_empty()
    );
    assert!(
        input(
            &mut g,
            Edge::Left {
                down: false,
                control: false
            },
            Vec2::new(1900., 900.),
            &l,
            &pages
        )
        .is_empty()
    );
    let p = position(&l, 7, 0, 0);
    input(
        &mut g,
        Edge::Left {
            down: true,
            control: false,
        },
        p,
        &l,
        &pages,
    );
    assert_eq!(
        input(
            &mut g,
            Edge::Left {
                down: false,
                control: false
            },
            Vec2::new(400., 300.),
            &l,
            &pages
        ),
        vec![Intent::TakeToPaperdoll(Cell::at(7, 0, 0))]
    );
}

#[test]
fn hold_transfer_is_fifo_survives_release_drops_only_manual_carry_and_caps_long_frames() {
    let pages = pages();
    let mut q = Transfers::default();
    let a = Cell::at(2, 1, 1);
    let b = Cell::at(2, 3, 2);
    assert!(
        q.step(&pages, 0.1, false, true, Some((a, false)), None)
            .is_none()
    );
    assert!(
        q.step(&pages, 0.1, false, true, Some((b, false)), None)
            .is_none()
    );
    assert_eq!(q.queue.len(), 2);
    assert_eq!(
        q.step(&pages, 2., false, false, None, None),
        Some(Intent::QuickTransfer(a))
    );
    assert_eq!(q.queue.len(), 1);
    assert_eq!(q.progress(), 0.);
    assert!(q.step(&pages, 0.1, false, false, None, None).is_none());
    assert_eq!(
        q.step(&pages, 0.3, false, false, None, None),
        Some(Intent::QuickTransfer(b))
    );
    q.step(&pages, 0.01, false, true, Some((a, false)), None);
    q.step(&pages, 0.01, false, true, Some((b, false)), None);
    assert!(
        q.step(&pages, 0.1, false, true, None, Some(Source::Grid(a)))
            .is_none()
    );
    assert_eq!(q.queue.front().unwrap().0, b);
    assert_eq!(q.queue.len(), 1);
    assert!((q.progress() - 0.1 / 0.35).abs() < 1e-5);
    q.step(&pages, 1., true, false, None, None);
    assert!(q.queue.is_empty());
}

#[test]
fn transfer_skips_stale_identity_and_area_but_keeps_same_cell_once() {
    let mut pages = pages();
    let mut q = Transfers::default();
    let a = Cell::at(2, 1, 1);
    q.step(&pages, 0.1, false, true, Some((a, false)), None);
    q.step(
        &pages,
        0.1,
        false,
        true,
        Some((Cell::at(2, 2, 1), false)),
        None,
    );
    assert_eq!(q.queue.len(), 1);
    pages[0].items[0].id = 5;
    assert!(q.step(&pages, 1., false, false, None, None).is_none());
    assert!(q.queue.is_empty());
    pages.push(InventoryPage {
        index: 8,
        width: 4,
        height: 4,
        items: vec![item(14, 0, 0, 1)],
    });
    assert!(
        q.step(
            &pages,
            1.,
            false,
            true,
            Some((Cell::at(8, 0, 0), false)),
            None
        )
        .is_none()
    );
}

#[test]
fn wheel_hits_whole_independent_column_and_small_split_marks_do_not_overlap() {
    let d = Dashboard::new(Vec2::new(1920., 1080.));
    assert_eq!(d.character.width(), 616.);
    assert!(d.split);
    let mut scroll = Scroll {
        content: [2000., 3000.],
        ..default()
    };
    let button = Vec2::new(d.container_x + 10., d.storage.min.y + 30.);
    assert!(scroll.wheel(&d, button, -1.));
    assert_eq!(scroll.offset, [0., 60.]);
    assert!(scroll.wheel(&d, button, 1.));
    assert_eq!(scroll.offset, [0., 0.]);
    assert_eq!(split_marks(5), vec![(1, "1/4"), (2, "1/2")]);
    assert!(!Dashboard::new(Vec2::new(1280., 720.)).split);
}
