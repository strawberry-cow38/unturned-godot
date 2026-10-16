use super::{lamps::*, notes::*, *};
mod visual;
use crate::{content::ObjData, world::WorldConfig};
use bevy::time::TimeUpdateStrategy;
use std::time::Duration;

fn content() -> std::path::PathBuf {
    WorldConfig::default().content_root
}
fn obj(name: &str) -> ObjData {
    ObjData::load(&content().join(format!("objects/{name}.obj"))).unwrap()
}

#[test]
fn kind_table_and_toggle_lamps_follow_lamp_light() {
    assert_eq!(kind_for("Light_0"), Kind::CeilingStrip);
    assert_eq!(kind_for("Light_1"), Kind::CeilingStrip);
    assert_eq!(kind_for("Lamp_1"), Kind::FloorShade);
    assert_eq!(kind_for("Lamp_0"), Kind::DeskBulb);
    assert_eq!(kind_for("Ceiling_Bulb_0"), Kind::CeilingBulb);
    assert_eq!(kind_for("Street_Light_0"), Kind::Generic);
    assert!(is_toggle(Kind::FloorShade) && is_toggle(Kind::DeskBulb));
    assert!(!is_toggle(Kind::CeilingStrip) && !is_toggle(Kind::CeilingBulb));
}

#[test]
fn only_an_exact_ex270_ez0_lamp_stands_as_a_deployable() {
    assert!(stands_as_deployable(Vec3::new(270., 347., 0.)));
    assert!(stands_as_deployable(Vec3::new(-90., 12., -0.)));
    assert!(stands_as_deployable(Vec3::new(270.4, 0., 359.6)));
    // PEI's one tilted standing lamp (row 2601) stays a prop LampLight.
    assert!(!stands_as_deployable(Vec3::new(295.019, 347.143, 87.635)));
    assert!(!stands_as_deployable(Vec3::new(270., 0., 0.6)));
    assert!(!stands_as_deployable(Vec3::new(0., 0., 0.)));
}

#[test]
fn init_jitter_is_a_deterministic_wear_and_reaction_fraction() {
    let (worn, frac) = jitter(Vec3::new(-768.07, 42.644, 772.867));
    assert!((0.95..=1.05).contains(&worn));
    assert!((0. ..1.).contains(&frac));
    assert_eq!(jitter(Vec3::new(-768.07, 0., 772.867)), (worn, frac), "Y is not hashed");
    assert_ne!(jitter(Vec3::new(3., 0., 4.)).0, worn);
}

#[test]
fn real_fixtures_carve_the_source_emitters() {
    // Light_0: the u>0.5, v>0.5 diffuser, its open top capped.
    let light = obj("Light_0");
    let (body, lens) = split(Kind::CeilingStrip, &light).expect("Light_0 diffuser texels");
    let carved = light.positions.len() - body.positions.len();
    assert!(carved > 0);
    assert!(lens.positions.len() >= carved, "the cap only adds faces");
    for t in 0..carved / 3 {
        let p = |k: usize| lens.uvs[t * 3 + k];
        assert!((0..3).all(|k| p(k)[0] > 0.5 && p(k)[1] > 0.5));
    }
    // Lamp_1: the top-left 181-grey shade (UV centroid u<0.5, v>0.5).
    let shade = obj("Lamp_1");
    let (_, e) = split(Kind::FloorShade, &shade).expect("Lamp_1 shade texel");
    for t in 0..e.positions.len() / 3 {
        let c = (Vec2::from_array(e.uvs[t * 3]) + Vec2::from_array(e.uvs[t * 3 + 1]) + Vec2::from_array(e.uvs[t * 3 + 2])) / 3.;
        assert!(c.x < 0.5 && c.y > 0.5);
    }
    // Lamp_0: no bulb texel; the head opening by geometry.
    let desk = obj("Lamp_0");
    let (_, bulb) = split(Kind::DeskBulb, &desk).expect("Lamp_0 head opening");
    assert!(bulb.min.z > 0.55, "on the head");
    assert!(split(Kind::Generic, &desk).is_none());
}

/// Godot 4.6.2 oracle: `--lamptest UG_LAMP_POSE=1 UG_LAMP_PITCH=270` prints the
/// built omni's world position and energy (bevy/artifacts/godot-lamp-*.log).
#[test]
fn omni_pose_and_energy_match_the_godot_lamptest() {
    for (name, mount, expected, energy) in [
        ("Light_0", 3., Vec3::new(0., 1.85, 0.), 2.09),
        ("Lamp_1", 0., Vec3::new(0., 1.988_276_5, 0.), 2.09),
        ("Lamp_0", 0., Vec3::new(0.279_058_87, 0.515_344_56, 0.), 1.045),
    ] {
        let kind = kind_for(name);
        let data = obj(name);
        let xf = Transform::from_xyz(0., mount, 0.).with_rotation(Quat::from_rotation_x(270f32.to_radians()));
        let (body, emit) = split(kind, &data).unwrap();
        let node = Vec3::new(0., mount, 0.);
        let at = light_world(kind, &xf, &body, Some(&emit), node);
        assert!(at.distance(expected) < 1e-4, "{name}: {at} vs Godot {expected}");
        let (worn, _) = jitter(node);
        let got = ENERGY * worn * if kind == Kind::DeskBulb { 0.5 } else { 1. };
        assert!((got - energy).abs() < 1e-5, "{name}: energy {got} vs {energy}");
    }
}

#[test]
fn cap_holes_closes_an_open_box_with_one_quad() {
    // Five faces of a unit box (top open): two triangles cap it.
    let mut lens = ObjData::default();
    let quads = [
        [[0., 0., 0.], [1., 0., 0.], [1., 1., 0.], [0., 1., 0.]],
        [[0., 0., 0.], [0., 0., 1.], [1., 0., 1.], [1., 0., 0.]],
        [[1., 0., 0.], [1., 0., 1.], [1., 1., 1.], [1., 1., 0.]],
        [[1., 1., 0.], [1., 1., 1.], [0., 1., 1.], [0., 1., 0.]],
        [[0., 1., 0.], [0., 1., 1.], [0., 0., 1.], [0., 0., 0.]],
    ];
    for q in quads {
        for tri in [[0, 1, 2], [0, 2, 3]] {
            for i in tri {
                lens.positions.push(q[i]);
                lens.normals.push([0., 0., 1.]);
                lens.uvs.push([0.75, 0.75]);
                lens.colors.push([1.; 4]);
            }
        }
    }
    let before = lens.positions.len();
    cap_holes(&mut lens);
    assert_eq!(lens.positions.len() - before, 6);
    assert!(lens.positions[before..].iter().all(|p| p[2] == 1.), "the missing top");
}

#[test]
fn grid_layer_reacts_after_its_own_delay_then_settles() {
    let mut grid = Grid::new(true, 0.5, 7);
    assert!(grid.last_lit && grid.shown_lit);
    grid.set_powered(false);
    assert!(grid.transitioning());
    assert!(grid.shown_lit, "nothing settles before the reaction delay");
    let reaction = REACTION_MAX * (0.15 + 0.85 * 0.5);
    let mut t = 0.;
    while t + 0.02 < reaction {
        grid.step(0.02);
        t += 0.02;
        assert!(grid.transitioning());
    }
    grid.step(0.05);
    assert!(!grid.transitioning() && !grid.shown_lit && !grid.last_lit);
    // The grid flapping back mid-transition re-aims the running delay.
    grid.set_powered(true);
    grid.step(0.1);
    grid.set_powered(false);
    for _ in 0..200 {
        grid.step(0.02);
    }
    assert!(!grid.last_lit && !grid.shown_lit);
}

#[test]
fn mains_believe_breakers_when_the_world_has_any() {
    let catalog = catalog();
    assert!(mains_live(&json!({}), &catalog), "no GridSource: the default-on flag");
    assert!(mains_live(&json!({"deployables":[{"defId":9200,"toggledOn":true},{"defId":9200,"toggledOn":false}]}), &catalog));
    assert!(!mains_live(&json!({"deployables":[{"defId":9200,"toggledOn":false}]}), &catalog));
    assert!(mains_live(&json!({"deployables":[{"defId":1918,"toggledOn":false}]}), &catalog), "not a breaker");
}

fn def(id: u16, name: &str, model: &str, fixture: &str, health: f32) -> Value {
    json!({"id":id,"name":name,"model":model,"size":{"x":1,"y":1,"z":1},"offset":0,"upright":false,
        "mount":"Floor","fixture":fixture,"isStorage":false,"isWindTurbine":false,"procBox":false,
        "meshEuler":{"x":180,"y":0,"z":0},"health":health})
}
fn catalog() -> ItemCatalog {
    let mut catalog = ItemCatalog::default();
    catalog.deployables = serde_json::from_value(json!([
        def(1918, "Desk Lamp", "Lamp_0", "None", 200.),
        def(1255, "Lamp", "Lamp_1", "None", 300.),
        def(9200, "Grid Power", "Circuit_0", "GridSource", 0.),
    ]))
    .unwrap();
    catalog
}

struct Scene {
    app: App,
    light: Entity,
    tilted: Entity,
    upright: Entity,
}
/// The three PEI lamp shapes, placed through `placement_transform` from their
/// retail rows (shifted near the origin), with the camera at the tilted lamp.
fn scene(look_at: &str) -> Scene {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        TransformPlugin,
        AssetPlugin::default(),
        bevy_rapier3d::prelude::RapierPhysicsPlugin::<bevy_rapier3d::prelude::NoUserData>::default()
            .in_fixed_schedule(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<StandardMaterial>()
    .insert_resource(TimeUpdateStrategy::ManualDuration(Duration::from_millis(20)))
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(UiState { screen: Screen::Playing, ..default() })
    .insert_resource(GameSnapshot(json!({"player":{"alive":true},
        "deployables":[{"netId":1,"defId":9200,"toggledOn":true}]})))
    .insert_resource(catalog())
    .insert_resource(WorldConfig::default())
    .init_resource::<crate::content::ContentCache>()
    .init_resource::<Settings>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<InteractionFocus>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .init_resource::<crate::info_billboard::FocusedInfo>()
    .init_resource::<DeviceState>()
    .init_resource::<Mains>()
    .init_resource::<Texts>()
    .init_resource::<Reader>()
    .configure_sets(Update, crate::entities::ApplyInteractions.after(CollectInteractions))
    .add_systems(
        Update,
        (
            reset_epoch,
            mains,
            lamps::attach,
            notes::attach,
            lamps::grid,
            |mut f: ResMut<InteractionFocus>, mut i: ResMut<crate::info_billboard::FocusedInfo>| {
                *f = default();
                i.0 = None;
            },
        )
            .chain()
            .before(CollectInteractions),
    )
    .add_systems(Update, collect_focus.in_set(CollectInteractions))
    .add_systems(
        Update,
        (scenery_info, lamps::press, notes::press)
            .chain()
            .after(CollectInteractions)
            .before(crate::entities::ApplyInteractions),
    );
    let world = app.world_mut();
    let material = world.resource_mut::<Assets<StandardMaterial>>().add(StandardMaterial::default());
    let mut place = |name: &str, index: usize, p: Vec3, e: Vec3| {
        let data = obj(name);
        let half = (data.max - data.min) * 0.5;
        let xf = crate::world::placement_transform(p, e, Vec3::ONE);
        let mesh = world.resource_mut::<Assets<Mesh>>().add(data.mesh());
        let entity = world
            .spawn((
                Mesh3d(mesh),
                MeshMaterial3d(material.clone()),
                xf,
                WorldObject { guid: String::new(), asset_name: name.into(), source_index: index },
                PlacementEuler(e),
                bevy_rapier3d::prelude::RigidBody::Fixed,
            ))
            .id();
        world.spawn((
            bevy_rapier3d::prelude::Collider::cuboid(half.x, half.y, half.z),
            Transform::from_translation((data.min + data.max) * 0.5),
            ChildOf(entity),
        ));
        (entity, xf)
    };
    let (light, _) = place("Light_0", 40, Vec3::new(8., 3.6, 0.), Vec3::new(270., 0., 0.));
    let (tilted, tilted_xf) = place("Lamp_1", 2601, Vec3::new(0., 0., 0.), Vec3::new(295.019, 347.143, 87.635));
    let (upright, upright_xf) = place("Lamp_0", 77, Vec3::new(-6., 0., 0.), Vec3::new(270., 90., 0.));
    let at = if look_at == "Lamp_0" { upright_xf } else { tilted_xf };
    let target = at.transform_point((obj(look_at).min + obj(look_at).max) * 0.5);
    world.spawn((PlayerCamera, Transform::from_translation(target + Vec3::new(0., 0.1, 1.6)).looking_at(target, Vec3::Y)));
    Scene { app, light, tilted, upright }
}
fn step(app: &mut App, frames: usize) {
    for _ in 0..frames {
        app.update();
    }
}
fn press_f(app: &mut App) {
    app.world_mut().resource_mut::<ButtonInput<KeyCode>>().press(KeyCode::KeyF);
    app.update();
    let mut keys = app.world_mut().resource_mut::<ButtonInput<KeyCode>>();
    keys.release(KeyCode::KeyF);
    keys.clear();
    app.update();
}
fn lit(app: &mut App, lamp: Entity) -> bool {
    let fixture = app.world().get::<LampFixture>(lamp).unwrap();
    let light = fixture.light;
    let (point, visibility) = app.world_mut().query::<(&PointLight, &Visibility)>().get(app.world(), light).unwrap();
    point.intensity > 0. && *visibility != Visibility::Hidden
}

#[test]
fn world_lamps_light_toggle_and_follow_the_accepted_mains() {
    let Scene { mut app, light, tilted, upright } = scene("Lamp_1");
    step(&mut app, 4);
    assert!(app.world().get::<LampFixture>(light).is_some(), "Light_0: a grid-fed LampLight");
    assert!(app.world().get::<Device>(light).is_none(), "the ceiling strip has no manual switch");
    assert!(app.world().get::<LampFixture>(tilted).is_some(), "the tilted Lamp_1 stays a prop LampLight");
    assert!(app.world().get::<SceneryLamp>(upright).is_some(), "an upright Lamp_0 is world scenery");
    assert!(app.world().get::<LampFixture>(upright).is_none(), "scenery is fed only by its unwired port: dark");
    assert!(lit(&mut app, light) && lit(&mut app, tilted));
    let energy = app.world().get::<LampFixture>(light).unwrap().omni_energy;
    assert!((energy / ENERGY - 1.).abs() <= 0.05 + 1e-6, "full energy times the wear");

    // The look ray picks the tilted lamp: silent offer, white whole-prop rim.
    {
        let focus = app.world().resource::<InteractionFocus>();
        let args = json!({"sourceIndex":2601});
        assert!(focus.selected_args("toggleLamp", &args));
        assert_eq!(focus.outlined(), Some((tilted, Color::WHITE)));
    }
    press_f(&mut app);
    assert!(!lit(&mut app, tilted), "F flips the lamp's own switch");
    assert!(lit(&mut app, light), "and nothing else");
    assert!(app.world().resource::<DeviceState>().lamp_off.contains(&2601));

    // The accepted breakers go off: both fixtures go dark after their delay.
    press_f(&mut app);
    assert!(lit(&mut app, tilted));
    app.world_mut().resource_mut::<GameSnapshot>().0["deployables"][0]["toggledOn"] = json!(false);
    // The 4 Hz mains scan sees it within 13 frames; the shortest reaction
    // delay (0.15 x 2.2 s) keeps the stutter going past frame 15.
    step(&mut app, 15);
    assert!(app.world().get::<LampFixture>(light).unwrap().grid.transitioning());
    assert!(app.world().get::<LampFixture>(tilted).unwrap().grid.transitioning());
    step(&mut app, 150);
    assert!(!lit(&mut app, light) && !lit(&mut app, tilted));
    // The manual switch cannot light a dead grid.
    press_f(&mut app);
    press_f(&mut app);
    assert!(!lit(&mut app, tilted));
}

#[test]
fn an_upright_desk_lamp_is_inert_world_scenery_with_its_billboard() {
    let Scene { mut app, upright, tilted, .. } = scene("Lamp_0");
    step(&mut app, 4);
    {
        let focus = app.world().resource::<InteractionFocus>();
        assert!(focus.selected_args("sceneryDeployable", &json!({"sourceIndex":77})));
        assert_eq!(focus.outlined(), Some((upright, crate::deployables::OUTLINE_COLOR)));
        let info = app.world().resource::<crate::info_billboard::FocusedInfo>();
        let view = info.0.as_ref().expect("the look-at billboard");
        assert_eq!(view.content.name, "Desk Lamp");
        assert_eq!(view.content.prompt, "", "no toggle, no pickup on scenery");
        assert_eq!(view.content.bars[0].map(|b| b.value), Some(1.));
        assert!(view.content.bars[1..].iter().all(Option::is_none));
    }
    press_f(&mut app);
    assert!(app.world().resource::<DeviceState>().lamp_off.is_empty(), "F does nothing here");
    assert!(lit(&mut app, tilted));
}

#[test]
fn a_note_reads_on_f_and_closes_on_f_or_escape() {
    let texts = Texts::parse(&std::fs::read_to_string(content().join("note_texts.tsv")).unwrap());
    let (name, lines) = &texts["53f724bd841b42b2839d476f07e1d3de"];
    assert_eq!(name, "Note - Military #1");
    let Scene { mut app, .. } = scene("Lamp_1");
    // Washington row 453: Note_6, right in front of the camera's eye.
    let data = obj("Note_6");
    let eye = app.world_mut().query_filtered::<&Transform, With<PlayerCamera>>().single(app.world()).unwrap().clone();
    let at = eye.translation + *eye.forward() * 0.8;
    let mut xf = crate::world::placement_transform(Vec3::ZERO, Vec3::new(270., 250., 0.), Vec3::ONE);
    xf.translation = at - xf.transform_point((data.min + data.max) * 0.5);
    let mesh = app.world_mut().resource_mut::<Assets<Mesh>>().add(data.mesh());
    let note = app
        .world_mut()
        .spawn((
            Mesh3d(mesh),
            xf,
            WorldObject { guid: "53f724bd841b42b2839d476f07e1d3de".into(), asset_name: "Note_6".into(), source_index: 453 },
            bevy_rapier3d::prelude::RigidBody::Fixed,
        ))
        .id();
    step(&mut app, 4);
    assert!(app.world().get::<Note>(note).is_some());
    let focus = app.world().resource::<InteractionFocus>();
    assert!(focus.selected_args("readNote", &json!({"sourceIndex":453})), "the padded look box is hit first");
    assert_eq!(focus.outlined(), Some((note, Color::WHITE)));
    press_f(&mut app);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Note);
    let open = app.world().resource::<Reader>().open.clone().unwrap();
    assert_eq!(open.0, "Military #1", "the title drops 'Note - '");
    assert_eq!(open.1, lines.join("\n"));
    press_f(&mut app);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing, "F closes it");
    assert!(app.world().resource::<Reader>().open.is_none());
    step(&mut app, 2);
    press_f(&mut app);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Note);
    app.world_mut().resource_mut::<UiState>().close_overlay();
    step(&mut app, 1);
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing, "Esc closes it");
    assert!(app.world().resource::<Reader>().open.is_none());
}
