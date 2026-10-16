//! Real X11 events through Winit, InputOrder, the actual Inventory controller,
//! text editor and cursor capture. No injected preview values or gameplay calls.
use super::*;
use bevy::window::{CursorGrabMode, CursorOptions};
#[derive(Resource, Default)]
pub(super) struct Steps {
    window: String,
    anchor: Vec2,
}
fn xdo(args: Vec<String>) -> String {
    let output = std::process::Command::new("xdotool")
        .args(args)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "xdotool: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout).unwrap().trim().into()
}
fn call(args: &[&str]) {
    xdo(args.iter().map(|s| s.to_string()).collect());
}
fn move_to(world: &World, point: Vec2) {
    xdo(vec![
        "mousemove".into(),
        "--window".into(),
        world.resource::<Steps>().window.clone(),
        format!("{:.0}", point.x),
        format!("{:.0}", point.y),
    ]);
}
fn screenshot(world: &mut World, c: &mut Capture, index: usize) {
    let menu = world.resource::<Menu>();
    let wallets: Vec<_> = menu
        .view
        .tiles
        .cells
        .iter()
        .filter(|cell| cell.tile.money)
        .map(|cell| cell.tile.amount)
        .collect();
    let facts = json!({"wallets":wallets,"previewAmount":menu.gesture.split_amount,"sent":c.sent,"state":world.resource::<GameSnapshot>().0});
    c.facts.push(facts);
    let done = c.done.clone();
    world
        .spawn(Screenshot::primary_window())
        .observe(save_to_disk(format!("{}-{index}.png", c.prefix)))
        .observe(move |_: On<ScreenshotCaptured>| *done.lock().unwrap() = true);
    c.pending = true;
}
pub(super) fn capture(world: &mut World) {
    let mut c = world.remove_resource::<Capture>().unwrap();
    c.frames += 1;
    assert!(c.frames < 600, "OS SpinBox stage{} timed out", c.stage);
    if c.pending {
        if !*c.done.lock().unwrap() {
            world.insert_resource(c);
            return;
        }
        *c.done.lock().unwrap() = false;
        c.pending = false;
        c.frames = 0;
    }
    let frame = world.resource::<FrameInput>();
    if !frame.events.is_empty() {
        let menu = world.resource::<Menu>();
        eprintln!(
            "[spinOS] stage={} frames={} selected={:?} typing={} amount={} field={:?} offset={:?} focus={:?} events={:?}",
            c.stage,
            c.frames,
            menu.gesture.selected,
            menu.typing,
            menu.gesture.split_amount,
            menu.hits.number,
            menu.offset,
            world.resource::<bevy::input_focus::InputFocus>().get(),
            frame.events
        );
    }
    let amount = world.resource::<Menu>().gesture.split_amount;
    let mut advance = false;
    match c.stage {
        0 if c.frames >= 40 => {
            let id = xdo(vec![
                "search".into(),
                "--name".into(),
                "^Original Inventory dashboard: accepted owner$".into(),
            ]);
            let id = id.lines().next().unwrap().to_string();
            call(&["windowfocus", "--sync", &id]);
            world.resource_mut::<Steps>().window = id;
            move_to(world, point(world, c.magazine));
            call(&["click", "3"]);
            advance = true;
        }
        1 if world.resource::<Menu>().gesture.selected.is_some() && c.frames >= 15 => {
            screenshot(world, &mut c, 0);
            advance = true;
        }
        2 if c.frames == 0 => {
            let field = world.resource::<Menu>().hits.number.unwrap();
            move_to(world, field.min + Vec2::new(20., 15.));
            call(&["click", "1"]);
        }
        2 if c.frames >= 5 => {
            call(&["key", "ctrl+a"]);
            call(&["type", "--clearmodifiers", "100/2+1"]);
            call(&["key", "Return"]);
            advance = true;
        }
        3 if amount == 51 && c.frames >= 8 => {
            screenshot(world, &mut c, 1);
            advance = true;
        }
        4 => {
            let field = world.resource::<Menu>().hits.number.unwrap();
            let anchor = spin::arrows(field)[0].center();
            world.resource_mut::<Steps>().anchor = anchor;
            move_to(world, anchor);
            call(&["mousedown", "1"]);
            call(&["mousemove_relative", "--", "0", "-3"]);
            advance = true;
        }
        5 if world.resource::<Menu>().spin.captured() => {
            assert_eq!(
                amount, 52,
                "arrow tap occurs before capture; threshold motion adds no delta"
            );
            let cursor = world
                .query_filtered::<&CursorOptions, With<PrimaryWindow>>()
                .single(world)
                .unwrap();
            assert_eq!(cursor.grab_mode, CursorGrabMode::Locked);
            assert!(!cursor.visible);
            call(&["mousemove_relative", "--", "0", "-20"]);
            advance = true;
        }
        6 if amount == 54 && c.frames >= 8 => {
            screenshot(world, &mut c, 2);
            advance = true;
        }
        7 => {
            call(&["mouseup", "1"]);
            advance = true;
        }
        8 if !world.resource::<Menu>().spin.captured() && c.frames >= 8 => {
            let (window, cursor) = world
                .query_filtered::<(&Window, &CursorOptions), With<PrimaryWindow>>()
                .single(world)
                .unwrap();
            assert_eq!(cursor.grab_mode, CursorGrabMode::None);
            assert!(cursor.visible);
            assert!(
                window
                    .cursor_position()
                    .unwrap()
                    .distance(world.resource::<Steps>().anchor)
                    < 2.
            );
            let menu = world.resource::<Menu>();
            let preview = menu.hits.split_preview.unwrap().center();
            let grid = menu.hits.grids.iter().find(|g| g.page == 2).unwrap();
            let page = menu.pages.iter().find(|p| p.index == 2).unwrap();
            let dest = (0..page.height)
                .flat_map(|y| (0..page.width).map(move |x| Cell::at(2, x, y)))
                .find(|cell| {
                    at(&menu.pages, *cell).is_none() && grid.clip.contains(point(world, *cell))
                })
                .unwrap();
            let dest = point(world, dest);
            move_to(world, preview);
            call(&["mousedown", "1"]);
            move_to(world, dest);
            call(&["mouseup", "1"]);
            advance = true;
        }
        9 if world
            .resource::<Menu>()
            .view
            .tiles
            .cells
            .iter()
            .filter(|cell| cell.tile.money)
            .count()
            == 2
            && c.frames >= 20 =>
        {
            screenshot(world, &mut c, 3);
            advance = true;
        }
        10 => {
            assert_eq!(c.facts[0]["wallets"], json!([150]));
            assert_eq!(c.facts[1]["wallets"], json!([150]));
            assert_eq!(c.facts[2]["wallets"], json!([150]));
            assert_eq!(c.facts[3]["wallets"], json!([96, 54]));
            assert_eq!(c.sent.len(), 1);
            assert_eq!(c.sent[0]["name"], "splitItem");
            std::fs::write(
                format!("{}.json", c.prefix),
                serde_json::to_vec_pretty(&c.facts).unwrap(),
            )
            .unwrap();
            world.write_message(AppExit::Success);
        }
        _ => {}
    }
    if advance {
        c.stage += 1;
        c.frames = 0;
    }
    world.insert_resource(c);
}
