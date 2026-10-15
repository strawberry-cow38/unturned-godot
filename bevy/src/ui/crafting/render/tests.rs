use super::*;
use bevy::asset::AssetPlugin;

pub(in crate::ui::crafting) fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins,AssetPlugin::default()))
        .init_asset::<Font>().init_asset::<Image>()
        .insert_resource(UiState{screen:Screen::Crafting,..default()})
        .insert_resource(UiModel{connected:true,map_name:"PEI".into(),..default()})
        .init_resource::<Settings>()
        .init_resource::<crate::character::nameplate::OriginalFont>()
        .insert_resource(Menu{viewport:Vec2::new(1024.,768.),browse:Browse{selected:1,..default()},options:json!({
            "view":{"categories":[{"name":"All","count":53},{"name":"Weapons","count":0},{"name":"Ammo","count":16}],
                "tiles":[{"index":1,"name":"Selected","known":true,"craftable":false},{"index":2,"name":"Ready","known":true,"craftable":true},{"index":3,"name":"Unavailable","known":false,"craftable":false}],
                "selected":{"name":"Output","outputAmount":2,"quantity":1,"maxQuantity":1,"craftable":false,"ingredients":[{"name":"Tool","amount":1,"total":2,"have":1,"consume":false}]},"header":"CRAFTING"},
            "queue":(0..8).map(|slot|json!({"slot":slot,"name":"Job","left":1.,"total":2.})).collect::<Vec<_>>()
        }),..default()})
        .add_systems(Update,build);
    app
}
#[test]
fn original_recipe_states_keep_solid_surfaces_green_rim_and_source_category_columns() {
    let mut app = app();
    app.update();
    let world = app.world_mut();
    let root = world
        .query_filtered::<Entity, With<Root>>()
        .single(world)
        .unwrap();
    assert_eq!(
        *world.get::<backdrop::Backdrop>(root).unwrap(),
        backdrop::Backdrop::dashboard(0.)
    );
    assert!(
        world
            .get::<BackgroundColor>(root)
            .is_none_or(|color| color.0.alpha() == 0.),
        "native dashboard root adds no opaque full-screen scrim"
    );
    let tiles: Vec<_> = world
        .query::<(&Action, &BackgroundColor, &Node, Option<&BorderColor>)>()
        .iter(world)
        .filter_map(|(action, bg, node, border)| {
            if let Action::Select(id) = action {
                Some((*id, bg.0, node.border, border.copied()))
            } else {
                None
            }
        })
        .collect();
    assert_eq!(tiles.iter().find(|(id, ..)| *id == 1).unwrap().1, SELECTED);
    let ready = tiles.iter().find(|(id, ..)| *id == 2).unwrap();
    assert_eq!(ready.1, TILE);
    assert_eq!(ready.2, UiRect::all(px(1)));
    assert_eq!(ready.3, Some(BorderColor::all(GOOD)));
    assert_eq!(
        tiles.iter().find(|(id, ..)| *id == 3).unwrap().1,
        UNAVAILABLE
    );
    let categories: Vec<_> = world
        .query::<&Action>()
        .iter(world)
        .filter_map(|a| {
            if let Action::Category(s) = a {
                Some(s.as_str())
            } else {
                None
            }
        })
        .collect();
    assert_eq!(
        categories,
        vec!["All", "Ammo"],
        "empty categories never create a row"
    );
    let texts: Vec<_> = world
        .query::<&Text>()
        .iter(world)
        .map(|t| t.0.clone())
        .collect();
    assert!(
        texts.contains(&"All".into()) && texts.contains(&"53".into()),
        "category name/count are independent aligned labels"
    );
    assert!(texts.contains(&"makes x2".into()));
    assert!(texts.contains(&"CRAFTING QUEUE".into()));
    let tooltip_font = world
        .query_filtered::<&TextFont, With<TooltipText>>()
        .single(world)
        .unwrap();
    assert_eq!(
        tooltip_font.font,
        world
            .resource::<crate::character::nameplate::OriginalFont>()
            .0
            .clone()
            .into(),
        "source tooltip requires OpenSans, including the middle-dot separator"
    );
    assert_eq!(tooltip_font.font_size, FontSize::Px(13.));
    let colors: Vec<_> = world
        .query::<&BackgroundColor>()
        .iter(world)
        .map(|c| c.0)
        .collect();
    assert!(colors.contains(&BG_SOLID) && colors.contains(&BAR_SOLID));
    let (_, bar_node, parent) = world
        .query::<(&Progress, &Node, &ChildOf)>()
        .single(world)
        .unwrap();
    assert_eq!(bar_node.width, percent(0));
    let holder = world.get::<Node>(parent.parent()).unwrap();
    assert_eq!(holder.left, px(1.5));
    assert_eq!(holder.top, px(38.5));
    assert_eq!(holder.width, px(39));
    assert_eq!(holder.height, px(2));
}
#[test]
fn lookup_owns_a_clearable_category_and_drag_scroll_preserves_queue_button_entities() {
    let mut app = app();
    {
        let mut menu = app.world_mut().resource_mut::<Menu>();
        menu.browse.lookup = "uses".into();
        menu.options["view"]["lookupLabel"] = json!("Uses of Saw");
    }
    app.update();
    let before: Vec<_> = app
        .world_mut()
        .query::<(Entity, &Action)>()
        .iter(app.world())
        .filter_map(|(e, a)| matches!(a, Action::Cancel(_)).then_some(e))
        .collect();
    assert_eq!(before.len(), 8);
    assert!(
        app.world_mut()
            .query::<&Text>()
            .iter(app.world())
            .any(|t| t.0 == "×  Uses of Saw")
    );
    let root = app
        .world_mut()
        .query_filtered::<Entity, With<Root>>()
        .single(app.world())
        .unwrap();
    let strip = app
        .world_mut()
        .query_filtered::<Entity, With<QueueStrip>>()
        .single(app.world())
        .unwrap();
    assert_eq!(
        app.world().get::<UiTransform>(strip).unwrap().translation,
        Val2::ZERO
    );
    app.world_mut().resource_mut::<Menu>().scroll[2] = 45.;
    app.update();
    assert!(app.world().get_entity(root).is_ok());
    assert!(
        before.iter().all(|e| app.world().get_entity(*e).is_ok()),
        "scroll must preserve the pressed button/capture until release"
    );
    let (_, node) = app
        .world_mut()
        .query_filtered::<(Entity, &Node), With<QueueArea>>()
        .single(app.world())
        .unwrap();
    assert_eq!(node.overflow, Overflow::clip());
    let mut menu = app.world_mut().resource_mut::<Menu>();
    menu.options["queue"] = json!([]);
    drop(menu);
    app.update();
    assert!(
        app.world_mut()
            .query::<&Text>()
            .iter(app.world())
            .any(|t| t.0 == "(empty)")
    );
}
