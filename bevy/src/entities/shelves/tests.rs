use super::*;
use std::path::Path;

#[cfg(test)]
mod visual;

#[test]
fn profiles_grid_dims_and_fallbacks_are_the_source_tables() {
    use layout::{grid_dims, profile};
    assert_eq!(grid_dims("Shelf_1", true), (8, 3), "gondola: 8 slots x 3 tiers");
    assert_eq!(grid_dims("Shelf_0", true), (3, 3));
    assert_eq!(grid_dims("Toaster_0", false), (2, 1), "the toaster's two-slot override");
    assert_eq!(grid_dims("Fridge_0", false), (8, 6), "SolidDefault keeps the roomy 8x6 grid");
    assert_eq!(profile("Odd_Display", true), profile("Shelf_1", true), "an unknown display shelf is a gondola");
    assert_eq!(profile("Odd_Solid", false).tier_y, &[0.5]);
    assert_eq!(profile("Shelf_0", true).tier_y, &[0.18, 0.48, 0.78]);
}

#[test]
fn nearest_axis_ties_to_the_lower_axis_and_negative_zero_is_positive() {
    use layout::nearest_axis;
    assert_eq!(nearest_axis(Vec3::new(0.5, 0.5, 0.5)), (0, 1.));
    assert_eq!(nearest_axis(Vec3::new(0., -0.7, 0.7)), (1, -1.));
    assert_eq!(nearest_axis(Vec3::new(-0., 0., -1.)), (2, -1.));
    assert_eq!(nearest_axis(Vec3::new(0., -0., 0.)), (0, 1.), "-0 >= 0 in the source");
}

#[test]
fn item_poses_load_from_the_ripped_icon_transforms() {
    let config = WorldConfig::default();
    let poses = layout::poses(&std::fs::read_to_string(config.content_root.join("items/item_poses.json")).unwrap());
    assert!(poses.len() > 200, "{}", poses.len());
    assert_eq!(poses[&2], layout::Pose { front: Vec3::new(0., 0., -1.), up: Vec3::X });
    let odd = layout::poses(r#"{"5":{"f":[1,0],"u":[0,1,0]},"x":{"f":[1,0,0],"u":[0,1,0]},"6":{"f":[1,0,0],"u":[0,1,0]}}"#);
    assert_eq!(odd.keys().copied().collect::<Vec<_>>(), [6], "two-component and non-integer rows are skipped");
}

#[test]
fn stand_and_lie_rules_follow_the_item_facts() {
    use layout::{Facts, lies_flat};
    let slab = Vec3::new(0.25, 0.11, 0.32);
    assert!(lies_flat(1, slab, Facts::default()), "a thin, non-round box lies");
    assert!(!lies_flat(91, slab, Facts { item_type: Some("water"), name: None }), "drinks stand even when flat");
    let tin = Vec3::new(0.32, 0.32, 0.13);
    assert!(!lies_flat(88, tin, Facts { item_type: Some("food"), name: None }), "a round tin stands");
    assert!(lies_flat(83, tin, Facts { item_type: Some("food"), name: None }), "_forceLie: chocolate");
    assert!(lies_flat(9, tin, Facts { item_type: Some("vest"), name: None }), "clothing folds flat");
    assert!(lies_flat(9, tin, Facts { item_type: Some("magazine"), name: None }));
    assert!(lies_flat(9, tin, Facts { item_type: Some("food"), name: Some("Ham Sandwich") }));
    assert!(!lies_flat(9, tin, Facts { item_type: Some("gun"), name: None }), "GUN is past GLASSES: not clothing");
}

#[test]
fn a_diagonal_icon_up_stands_the_item_on_its_longest_axis() {
    use layout::{Facts, Pose, orient};
    let carrot_like = Pose { front: Vec3::X, up: Vec3::new(0., 0.7071, 0.7071) };
    let food = Facts { item_type: Some("food"), name: None };
    let m = orient(1, Vec3::new(0.1, 0.12, 0.4), Some(carrot_like), food);
    assert_eq!(m.z_axis, Vec3::Y, "local Z (the longest) is world up, not the tied Y");
    assert_eq!(m.x_axis, Vec3::Z, "icon front -> the aisle");
    assert!((m.determinant() - 1.).abs() < 1e-6);
    let square = orient(1, Vec3::new(0.1, 0.12, 0.4), Some(Pose { front: Vec3::X, up: Vec3::Y }), food);
    assert_eq!(square.y_axis, Vec3::Y);
}

#[test]
fn digest_cells_decode_the_linear_index_rotation_and_unknown_items() {
    let mut catalog = ItemCatalog::default();
    for (id, w, h) in [(13u16, 1u8, 1u8), (277, 2, 1)] {
        catalog.items.insert(id, serde_json::from_value(json!({"id":id,"name":"x","type":"food","width":w,"height":h})).unwrap());
    }
    let display = json!([{"cell":13,"itemId":13,"rot":0},{"cell":2,"itemId":277,"rot":1},{"cell":0,"itemId":277,"rot":2},{"cell":4,"itemId":9999,"rot":0}]);
    let cells = cells(&display, 8, &catalog);
    assert_eq!(cells, [
        Cell { x: 5, y: 1, eff_w: 1, item_id: 13 },
        Cell { x: 2, y: 0, eff_w: 1, item_id: 277 },
        Cell { x: 0, y: 0, eff_w: 2, item_id: 277 },
    ], "y*Width+x; an odd rot takes size_y; Assets.makeLoot null is skipped");
    assert_eq!(cells[0].key(), 5 << 8 | 1);
    assert!(super::cells(&display, 0, &catalog).is_empty());
}

#[test]
fn place_mirrors_the_grid_and_hides_cells_beyond_the_slots() {
    let shelf = (Vec3::new(-2.5, 0., -0.5), Vec3::new(2.5, 2.6, 0.5));
    let can = (Vec3::splat(-0.1), Vec3::splat(0.1));
    let p = layout::profile("Shelf_1", true);
    let top = layout::place(p, shelf, 0, 0, 1, can, Mat3::IDENTITY).unwrap();
    let bottom = layout::place(p, shelf, 0, 2, 1, can, Mat3::IDENTITY).unwrap();
    assert!(top.transform.translation.y > bottom.transform.translation.y, "UI top row -> top tier");
    assert!(layout::place(p, shelf, 8, 0, 1, can, Mat3::IDENTITY).is_none(), "column past PerTier");
    assert!(layout::place(p, shelf, 0, 3, 1, can, Mat3::IDENTITY).is_none(), "row past the tiers");
    let wide = layout::place(p, shelf, 7, 0, 3, can, Mat3::IDENTITY).unwrap();
    let last = layout::place(p, shelf, 7, 0, 1, can, Mat3::IDENTITY).unwrap();
    assert_eq!(wide.transform.translation.x, last.transform.translation.x, "a wide item is clamped to the end");
}

#[test]
fn tag_tint_lerps_the_rarity_colour_35_percent_toward_white() {
    // Godot dump: Carjack (uncommon) Modulate.
    let c = tag_tint(items::rarity_color("uncommon")).to_srgba();
    assert!((c.red - 0.42902).abs() < 1e-4 && (c.green - 0.69412).abs() < 1e-4 && (c.blue - 0.42902).abs() < 1e-4, "{c:?}");
    let white = tag_tint(Color::WHITE).to_srgba().to_f32_array();
    assert!(white.iter().all(|v| (v - 1.).abs() < 1e-6), "common items keep a white tag: {white:?}");
}

fn item(net_id: u32, x: u8) -> ShelfItem {
    ShelfItem {
        net_id,
        x,
        y: 0,
        item_id: 13,
        name: "Canned Beans".into(),
        tint: Color::WHITE,
        center: Vec3::ZERO,
        half: Vec3::splat(0.1),
        tag: Vec3::ZERO,
    }
}
#[test]
fn the_ray_claims_the_nearest_box_and_the_sphere_refines_at_the_ray_end() {
    let (a, b) = (item(1, 0), item(1, 1));
    let at = |p: Vec3| Mat4::from_translation(p);
    let items = [(&a, at(Vec3::new(0., 0., -1.5))), (&b, at(Vec3::new(0., 0., -1.0)))];
    let (hit, t) = pick(&items, Vec3::ZERO, Vec3::NEG_Z, LOOK_REACH).unwrap();
    assert_eq!(hit, 1, "nearest box along the ray");
    assert!((t - 0.9).abs() < 1e-5);
    assert!(pick(&items[..1], Vec3::ZERO, Vec3::NEG_Z, 1.2).is_none(), "the world stopped the ray first");
    // Ray misses (0.2 m to the side) but the box is inside the 0.16 m sphere at the 2.6 m end.
    let c = item(2, 0);
    let side = [(&c, at(Vec3::new(0.2, 0., -2.6)))];
    assert_eq!(pick(&side, Vec3::ZERO, Vec3::NEG_Z, LOOK_REACH), Some((0, LOOK_REACH - LOOK_SPHERE)));
    let far = [(&c, at(Vec3::new(0.3, 0., -2.6)))];
    assert!(pick(&far, Vec3::ZERO, Vec3::NEG_Z, LOOK_REACH).is_none(), "0.2 m clear of the sphere");
    // A scaled model: the box and the sphere both scale with it.
    let scaled = [(&c, Mat4::from_scale_rotation_translation(Vec3::splat(0.5), Quat::IDENTITY, Vec3::new(0.12, 0., -1.)))];
    assert!(pick(&scaled, Vec3::ZERO, Vec3::NEG_Z, LOOK_REACH).is_none(), "half-size box ends at x=0.07");
}

#[test]
fn a_shelf_item_is_an_ordinary_silent_candidate_above_the_shelf_and_harvest() {
    let mut focus = InteractionFocus::default();
    focus.offer(1.4, "Open Store Shelf", "openStorage", json!({"netId":9}), true);
    offer_silent(&mut focus, 1.2, json!({"netId":9,"x":2,"y":1}));
    focus.fall_through("harvestCrop", json!({"netId":5}));
    let c = focus.candidate.as_ref().unwrap();
    assert_eq!((c.action.as_str(), c.args.clone()), (TAKE, json!({"netId":9,"x":2,"y":1})), "crop harvest overrides only open_storage");
    assert!(!c.open_storage && !c.manual && !c.show_binding && c.label.is_empty(), "no HUD prompt, F dispatches");
}

// ---- integrated: the real bridge catalogue / authority ----

fn bridge() -> CoreBridge {
    let bridge = CoreBridge::launch(Path::new(env!("CARGO_MANIFEST_DIR"))).unwrap();
    bridge
        .request("init", json!({"contentRoot":WorldConfig::default().content_root,"name":"shelves","spawn":[0,0,0]}))
        .unwrap();
    bridge
}
pub(super) fn catalog(bridge: &CoreBridge) -> ItemCatalog {
    let result = bridge.request("catalog", json!({})).unwrap()["result"].clone();
    let mut catalog = ItemCatalog::default();
    let items: Vec<ItemVisual> = serde_json::from_value(result["items"].clone()).unwrap();
    catalog.items = items.into_iter().map(|i| (i.id, i)).collect();
    catalog.containers = serde_json::from_value(result["containers"].clone()).unwrap();
    catalog
}
pub(super) fn display_kind(catalog: &ItemCatalog, mesh: &str) -> u16 {
    catalog.containers.iter().find(|k| k.mesh == mesh && k.display).unwrap().kind_id
}
fn app(bridge: CoreBridge, catalog: ItemCatalog) -> App {
    let config = WorldConfig::default();
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin { file_path: config.content_root.to_string_lossy().into(), ..default() }))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .insert_resource(bridge)
        .insert_resource(config)
        .insert_resource(catalog)
        .insert_resource(GameSnapshot(Value::Null))
        .init_resource::<UiModel>()
        .insert_resource(UiState { screen: crate::ui::Screen::Playing, ..default() })
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<InteractionFocus>()
        .init_resource::<ContentCache>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<items::Models>()
        .configure_sets(Update, ApplyInteractions.after(CollectInteractions))
        .add_systems(Update, (reset_focus.before(sync).before(CollectInteractions), apply_interaction.in_set(ApplyInteractions)));
    // The production schedule: digest sync, then the look offer, then F.
    install(&mut app);
    app
}
fn shelf_items(app: &mut App) -> Vec<(ShelfItem, Transform)> {
    let mut q = app.world_mut().query::<(&ShelfItem, &Transform)>();
    q.iter(app.world()).map(|(i, t)| (i.clone(), *t)).collect()
}
fn close(a: Vec3, b: &Value, what: &str, name: &str) {
    let b = Vec3::new(b[0].as_f64().unwrap() as f32, b[1].as_f64().unwrap() as f32, b[2].as_f64().unwrap() as f32);
    assert!((a - b).abs().max_element() < 2e-3, "{name} {what}: native {a} vs Godot {b}");
}

/// Main.cs UG_SHELFDEMO, dumped from the actual Godot 4.6.2 StoreShelf (front
/// gondola + its yaw-180 back twin, DebugDisplay packing). The same cells go
/// through the production digest -> sync -> PlaceItem path here.
#[test]
fn production_sync_reproduces_the_godot_shelfdemo_layout_item_for_item() {
    let bridge = bridge();
    let catalog = catalog(&bridge);
    let kind = display_kind(&catalog, "Shelf_1");
    let golden: Vec<Value> = serde_json::from_str(include_str!("godot_shelfdemo.json")).unwrap();
    let digest = |yaw: i64| {
        golden
            .iter()
            .filter(|g| g["yaw"].as_i64().unwrap().abs() == yaw)
            .map(|g| {
                let key = g["cell"].as_u64().unwrap();
                json!({"cell":(key & 0xff) * 8 + (key >> 8),"itemId":g["id"],"rot":0})
            })
            .collect::<Vec<_>>()
    };
    let rows = json!({"connected":true,"containers":[
        {"netId":1,"kindId":kind,"position":[0,0,-4.5],"yaw":0,"width":8,"height":3,"display":digest(0)},
        {"netId":2,"kindId":kind,"position":[0,0,-4.5],"yaw":180,"width":8,"height":3,"display":digest(180)}]});
    let mut app = app(bridge, catalog);
    app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&rows);
    app.world_mut().resource_mut::<GameSnapshot>().0 = rows;
    app.update();
    let placed = shelf_items(&mut app);
    assert_eq!(placed.len(), golden.len(), "every Godot-placed item, nothing more");
    for g in &golden {
        let (net, key) = (if g["yaw"] == 0 { 1 } else { 2 }, g["cell"].as_u64().unwrap() as u16);
        let name = g["name"].as_str().unwrap();
        let (item, t) = placed.iter().find(|(i, _)| i.net_id == net && (u16::from(i.x) << 8 | u16::from(i.y)) == key).unwrap_or_else(|| panic!("{name} missing"));
        assert_eq!(item.name, name);
        let basis = Mat3::from_quat(t.rotation) * t.scale.x;
        assert!((t.scale - Vec3::splat(t.scale.x)).length() < 1e-6, "uniform scale");
        close(basis.x_axis, &g["bx"], "basis X", name);
        close(basis.y_axis, &g["by"], "basis Y", name);
        close(basis.z_axis, &g["bz"], "basis Z", name);
        close(t.translation, &g["origin"], "origin", name);
        let tag = Vec3::new(g["tag"][0].as_f64().unwrap() as f32, g["tag"][1].as_f64().unwrap() as f32, g["tag"][2].as_f64().unwrap() as f32);
        close(item.tag, &json!((t.translation + basis * tag).to_array()), "tag", name);
        let (a, b) = (&g["box"], &g["aabb"]);
        close(item.center, &json!([a[0], a[1], a[2]]), "hitbox centre", name);
        close(item.half * 2., &json!([a[3], a[4], a[5]]), "hitbox size", name);
        let (min, max) = app.world().resource::<items::Models>().bounds(item.item_id);
        close(min, &json!([b[0], b[1], b[2]]), "mesh AABB position", name);
        close(max - min, &json!([b[3], b[4], b[5]]), "mesh AABB size", name);
        let m = &g["modulate"];
        close(item.tint.to_srgba().to_vec3(), &json!([m[0], m[1], m[2]]), "tag modulate", name);
    }
    // The container's catalogue dims are the StoreShelf.GridDims of its mesh.
    let catalog = app.world().resource::<ItemCatalog>();
    for k in &catalog.containers {
        assert_eq!((k.width, k.height), layout::grid_dims(&k.mesh, k.display), "{} kind {}", k.mesh, k.kind_id);
    }
}

fn tick(app: &mut App) -> Value {
    let mut state = Value::Null;
    for _ in 0..30 {
        state = app.world().resource::<CoreBridge>().request("tick", json!({})).unwrap()["state"].clone();
    }
    app.world_mut().resource_mut::<UiModel>().adopt_snapshot(&state);
    app.world_mut().resource_mut::<GameSnapshot>().0 = state.clone();
    state
}
fn action(app: &App, name: &str, args: Value) -> Value {
    app.world().resource::<CoreBridge>().request("action", json!({"name":name,"args":args})).unwrap()
}
fn press_f(app: &mut App) {
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::KeyF);
    app.update();
    let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
    keys.release(KeyCode::KeyF);
    keys.clear();
}
fn bag_count(state: &Value, id: u64) -> usize {
    state["inventory"]["pages"].as_array().unwrap().iter()
        .filter(|p| p["index"].as_u64().unwrap() < 7)
        .flat_map(|p| p["items"].as_array().unwrap().iter())
        .filter(|i| i["itemId"].as_u64().or(i["id"].as_u64()) == Some(id))
        .count()
}
fn display(state: &Value) -> Value {
    state["containers"][0]["display"].clone()
}

/// The whole intent loop through the real authority: a server-stocked display
/// cell becomes a placed model; looking at it offers F without a prompt; F is
/// only the TakeFromStorage intent; out of reach the server refuses; in reach
/// the accepted digest drops the cell and the model leaves, into the bag.
#[test]
fn f_on_a_looked_at_shelf_item_takes_that_cell_through_the_authority() {
    let bridge = bridge();
    let catalog = catalog(&bridge);
    let mut app = app(bridge, catalog);
    let registered = action_services(&app);
    assert_eq!(registered["result"]["containers"], 1, "{registered}");
    tick(&mut app);
    // Stock it the way a player would: give, open, move into the shelf grid at (3,1), close.
    action(&app, "console", json!({"text":"give 13"}));
    let state = tick(&mut app);
    assert_eq!(bag_count(&state, 13), 1);
    let net_id = state["containers"][0]["netId"].as_u64().unwrap();
    let bag = state["inventory"]["pages"].as_array().unwrap().iter()
        .find_map(|p| p["items"].as_array().unwrap().iter().find(|i| i["id"] == 13).map(|i| (p["index"].clone(), i["x"].clone(), i["y"].clone())))
        .unwrap();
    action(&app, "openStorage", json!({"netId":net_id}));
    tick(&mut app);
    action(&app, "moveItem", json!({"page0":bag.0,"x0":bag.1,"y0":bag.2,"page1":7,"x1":3,"y1":1,"rot1":0}));
    tick(&mut app);
    action(&app, "closeStorage", json!({}));
    let state = tick(&mut app);
    assert_eq!(display(&state), json!([{"cell":11,"itemId":13,"rot":0}]), "the server's digest: y*8+x");
    app.update();
    let placed = shelf_items(&mut app);
    assert_eq!(placed.len(), 1);
    let (item, local) = placed[0].clone();
    assert_eq!((item.net_id as u64, item.x, item.y, item.item_id), (net_id, 3, 1, 13));
    // Aim the camera at the model from the aisle (the shelf faces +Z).
    let root = Transform::from_translation(Vec3::new(0., 0., -1.5));
    let target = item_matrix(&root, &local).transform_point3(item.center);
    let eye = target + Vec3::new(0.2, 0.35, 1.1);
    app.world_mut().spawn((PlayerCamera, Transform::from_translation(eye).looking_at(target, Vec3::Y)));
    app.world_mut().spawn((PlayerBody::for_motion_test(Vec3::ZERO, true, "stand"), Transform::default()));
    app.update();
    let args = json!({"netId":net_id,"x":3,"y":1});
    assert!(app.world().resource::<InteractionFocus>().candidate.as_ref().is_some_and(|c| c.action == TAKE && c.args == args));
    assert_eq!(app.world().resource::<UiModel>().interaction, "", "the source shows the tag, not a HUD prompt");
    assert_eq!(app.world().resource::<FocusedShelfItem>().0.as_ref().map(|f| f.2.as_str()), Some("Canned Beans"));
    // Out of the 4 m StorageReach the server refuses; nothing moves.
    action(&app, "console", json!({"text":"teleport 30 0 30"}));
    tick(&mut app);
    press_f(&mut app);
    let state = tick(&mut app);
    app.update();
    assert_eq!(display(&state).as_array().unwrap().len(), 1, "refused beyond StorageReach");
    assert_eq!(bag_count(&state, 13), 0);
    assert_eq!(shelf_items(&mut app).len(), 1);
    action(&app, "console", json!({"text":"teleport 0 0 0"}));
    tick(&mut app);
    app.update();
    press_f(&mut app);
    assert_eq!(shelf_items(&mut app).len(), 1, "F is only an intent: the model waits for the authority");
    let state = tick(&mut app);
    app.update();
    assert_eq!(display(&state), json!([]), "the accepted take drops the cell from the digest");
    assert_eq!(bag_count(&state, 13), 1, "...into the taker's bag");
    assert!(shelf_items(&mut app).is_empty(), "and the model leaves with it");
}
fn action_services(app: &App) -> Value {
    app.world()
        .resource::<CoreBridge>()
        .request("worldServices", json!({"containers":[
            {"mesh":"Shelf_1","table":6,"display":true,"label":"Store Shelf","position":[0,0,-1.5],"yaw":0}]}))
        .unwrap()
}
