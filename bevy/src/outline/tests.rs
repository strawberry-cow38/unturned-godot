use super::*;
use serde_json::json;

#[test]
fn only_the_winning_candidate_claims_the_silhouette_and_colour() {
    let mut focus = InteractionFocus::default();
    let (item, shelf) = (Entity::from_raw_u32(1).unwrap(), Entity::from_raw_u32(2).unwrap());
    focus.offer(2., "Open Shelf", "openStorage", json!({"netId":9}), true);
    focus.outline("openStorage", &json!({"netId":9}), shelf, Color::WHITE);
    assert_eq!(focus.outlined(), Some((shelf, Color::WHITE)));
    focus.offer(1., "Pick up", "pickupItem", json!({"netId":4}), false);
    assert_eq!(focus.outlined(), None, "a nearer target replaces the claim");
    focus.outline("openStorage", &json!({"netId":9}), shelf, Color::WHITE);
    assert_eq!(focus.outlined(), None, "a loser cannot claim the rim");
    let rare = Color::srgb(0.3, 0.4, 0.98);
    focus.outline("pickupItem", &json!({"netId":4}), item, rare);
    assert_eq!(focus.outlined(), Some((item, rare)));
}

#[test]
fn mirrors_every_mesh_under_the_target_on_the_outline_layer_and_follows_focus() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_resource::<Outline>()
        .add_systems(Update, mirror);
    let mesh = app.world_mut().resource_mut::<Assets<Mesh>>().add(Cuboid::default());
    let body = app.world_mut().spawn((Mesh3d(mesh.clone()), Transform::default())).id();
    let leaf = app.world_mut().spawn((Mesh3d(mesh.clone()), Transform::default(), ChildOf(body))).id();
    let other = app.world_mut().spawn((Mesh3d(mesh), Transform::default())).id();
    app.world_mut().resource_mut::<Outline>().target = Some(body);
    app.update();
    let mut q = app.world_mut().query::<(&ChildOf, &RenderLayers, &MeshMaterial3d<StandardMaterial>)>();
    let mirrors: Vec<_> = q.iter(app.world()).map(|(p, l, m)| (p.parent(), l.clone(), m.0.clone())).collect();
    assert_eq!(mirrors.iter().filter(|m| m.1 == RenderLayers::layer(OUTLINE_LAYER)).count(), 2);
    let parents: Vec<_> = mirrors.iter().filter(|m| m.1 == RenderLayers::layer(OUTLINE_LAYER)).map(|m| m.0).collect();
    assert!(parents.contains(&body) && parents.contains(&leaf), "body and swinging leaf (whole prop)");
    let white = app.world().resource::<Assets<StandardMaterial>>().get(&mirrors.iter().find(|m| m.0 == body && m.1 == RenderLayers::layer(OUTLINE_LAYER)).unwrap().2).unwrap().clone();
    assert!(white.unlit && white.cull_mode.is_none() && white.base_color == Color::WHITE);
    app.world_mut().resource_mut::<Outline>().target = Some(other);
    app.update();
    let count = |app: &mut App| app.world_mut().query::<&OutlineMirror>().iter(app.world()).count();
    assert_eq!(count(&mut app), 1, "the previous silhouette leaves with the focus");
    app.world_mut().resource_mut::<Outline>().target = None;
    app.update();
    assert_eq!(count(&mut app), 0);
}

#[test]
fn the_outline_graphics_option_turns_the_whole_pass_off() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), bevy::window::WindowPlugin::default()))
        .init_asset::<Mesh>()
        .init_asset::<StandardMaterial>()
        .init_asset::<Image>()
        .init_asset::<OutlineMaterial>()
        .init_resource::<Outline>()
        .init_resource::<crate::settings::Settings>()
        .add_systems(Update, (mirror, present).chain());
    let mesh = app.world_mut().resource_mut::<Assets<Mesh>>().add(Cuboid::default());
    let target = app.world_mut().spawn((Mesh3d(mesh), Transform::default())).id();
    app.world_mut().spawn((PlayerCamera, Transform::default(), Projection::default()));
    app.world_mut().resource_mut::<Outline>().target = Some(target);
    let active = |app: &mut App| {
        let mask = app.world().resource::<Outline>().mask.clone().unwrap();
        app.world().get::<Camera>(mask.camera).unwrap().is_active
    };
    app.update();
    app.update(); // the mask camera spawns through commands on the first frame
    assert!(active(&mut app), "a claimed silhouette runs the mask pass");
    app.world_mut().resource_mut::<crate::settings::Settings>().graphics.outline = false;
    app.update();
    assert!(!active(&mut app), "Outline off: no mask, no rim");
}

#[test]
fn net_id_claims_follow_the_winner_and_vehicles_carry_their_dat_rarity() {
    let mut focus = InteractionFocus::default();
    let (car, crate_) = (Entity::from_raw_u32(5).unwrap(), Entity::from_raw_u32(6).unwrap());
    focus.offer(1., "Enter Roadster", "enterVehicle", json!({"netId":40,"seat":1}), false);
    focus.outline_net_id(41, crate_, Color::WHITE);
    assert_eq!(focus.outlined(), None, "another net id cannot claim");
    let rare = crate::entities::items::rarity_color("rare");
    focus.outline_net_id(40, car, rare);
    assert_eq!(focus.outlined(), Some((car, rare)), "any action the winner offered for that id");
    let rarities: Vec<_> = ["roadster", "bus", "sedan"]
        .iter()
        .map(|k| crate::vehicle::data::named(k).map(|d| d.spec.rarity.clone()))
        .collect();
    assert_eq!(rarities, [Some("RARE".into()), Some("UNCOMMON".into()), Some("COMMON".into())]);
}
