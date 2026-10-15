use super::*;
fn item(id: u16) -> crate::entities::ItemVisual {
    serde_json::from_value(json!({"id":id,"name":"Rifle","type":"gun"})).unwrap()
}
#[test]
fn hold_menu_closes_on_release_replacement_or_session_and_refuses_reload() {
    let mut app = App::new();
    app.add_plugins(MinimalPlugins)
        .init_resource::<Settings>()
        .init_resource::<ButtonInput<KeyCode>>()
        .init_resource::<ButtonInput<MouseButton>>()
        .init_resource::<UiState>()
        .init_resource::<ItemCatalog>()
        .init_resource::<WorldSessionEpoch>()
        .init_resource::<GameSnapshot>()
        .init_resource::<ViewmodelStatus>()
        .init_resource::<Menu>()
        .add_systems(Update, input);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Playing;
    app.world_mut()
        .resource_mut::<ItemCatalog>()
        .items
        .insert(4, item(4));
    {
        let mut status = app.world_mut().resource_mut::<ViewmodelStatus>();
        status.held = 4;
        status.ready = true;
    }
    app.world_mut().resource_mut::<GameSnapshot>().0 = json!({"connected":true,"player":{"alive":true},"weapon":{"selectionVersion":3,"reloading":true}});
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyT);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["reloading"] = json!(false);
    app.update();
    assert_eq!(
        app.world().resource::<UiState>().screen,
        Screen::Attachments
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.update();
    assert_eq!(
        app.world().resource::<UiState>().screen,
        Screen::Attachments
    );
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .release(KeyCode::KeyT);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .press(KeyCode::KeyT);
    app.update();
    app.world_mut().resource_mut::<GameSnapshot>().0["weapon"]["selectionVersion"] = json!(4);
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
    app.world_mut()
        .resource_mut::<ButtonInput<KeyCode>>()
        .clear();
    app.update();
    assert_eq!(app.world().resource::<UiState>().screen, Screen::Playing);
}
#[test]
fn rendered_duplicate_magazine_buttons_retain_exact_tokens_and_rounds() {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default()))
        .init_asset::<Image>()
        .init_resource::<Menu>()
        .init_resource::<Settings>()
        .init_resource::<UiState>()
        .init_resource::<WorldConfig>()
        .init_resource::<ItemCatalog>()
        .init_resource::<GameSnapshot>()
        .add_message::<UiIntent>()
        .add_systems(Update, build)
        .add_observer(activate);
    app.world_mut().resource_mut::<UiState>().screen = Screen::Attachments;
    let options = json!({"gunToken":100,"selectionVersion":3,"gunName":"Rifle","busy":false,"slots":[{
            "name":"Magazine","visible":true,"available":true,"detachable":true,"color":[1,1,1,1],"kind":"magazine",
            "choices":[{"id":6,"page":2,"x":0,"y":0,"amount":30,"instanceToken":101},
                {"id":6,"page":2,"x":2,"y":0,"amount":12,"instanceToken":102}]}]});
    app.world_mut().resource_mut::<Menu>().options = options.clone();
    app.world_mut().resource_mut::<Menu>().selection = json!(3);
    app.world_mut().resource_mut::<GameSnapshot>().0 =
        json!({"connected":true,"player":{"alive":true},"weapon":{"selectionVersion":3}});
    app.update();
    let world = app.world_mut();
    let buttons: Vec<_> = world
        .query::<(Entity, &Action, &Label)>()
        .iter(world)
        .filter(|(_, a, _)| a.name == "installAttachment")
        .map(|(e, a, l)| (e, a.args.clone(), l.0.clone()))
        .collect();
    assert_eq!(buttons.len(), 2);
    let short = buttons
        .iter()
        .find(|(_, a, _)| a["instanceToken"] == 102)
        .unwrap();
    assert!(short.2.contains("12 rounds"));
    // Accepted state can change before the next widget rebuild. A stale
    // enabled Button must not emit an action through the observer.
    world.resource_mut::<Menu>().pending = true;
    world.trigger(Activate { entity: short.0 });
    assert_eq!(
        world.resource_mut::<Messages<UiIntent>>().drain().count(),
        0
    );
    world.resource_mut::<Menu>().pending = false;
    world.resource_mut::<GameSnapshot>().0["weapon"]["reloading"] = json!(true);
    world.trigger(Activate { entity: short.0 });
    assert_eq!(
        world.resource_mut::<Messages<UiIntent>>().drain().count(),
        0
    );
    world.resource_mut::<GameSnapshot>().0["weapon"]["reloading"] = json!(false);
    world.resource_mut::<GameSnapshot>().0["weapon"]["selectionVersion"] = json!(4);
    world.trigger(Activate { entity: short.0 });
    assert_eq!(
        world.resource_mut::<Messages<UiIntent>>().drain().count(),
        0
    );
    world.resource_mut::<GameSnapshot>().0["weapon"]["selectionVersion"] = json!(3);
    world.trigger(Activate { entity: short.0 });
    let messages: Vec<_> = world.resource_mut::<Messages<UiIntent>>().drain().collect();
    assert!(
        matches!(&messages[0],UiIntent::Action{name,args} if name=="installAttachment"&&args["instanceToken"]==102&&args["gunToken"]==100&&args["selectionVersion"]==3&&args["x"]==2)
    );
    assert_eq!(world.resource::<Menu>().options, options);
    world.resource_mut::<UiState>().screen = Screen::Playing;
    app.update();
    let world = app.world_mut();
    assert_eq!(world.query::<&Hook>().iter(world).count(), 0);
}
#[test]
fn original_magazine_alpha_bounds_choose_stanag_but_not_upright_drum() {
    use bevy::{
        asset::RenderAssetUsages,
        image::{CompressedImageFormats, ImageSampler, ImageType},
    };
    for (id, wide) in [(6, true), (17, false)] {
        let bytes = std::fs::read(
            WorldConfig::default()
                .content_root
                .join(format!("items/icons/{id}.png")),
        )
        .unwrap();
        let image = Image::from_buffer(
            &bytes,
            ImageType::Extension("png"),
            CompressedImageFormats::NONE,
            true,
            ImageSampler::Default,
            RenderAssetUsages::MAIN_WORLD,
        )
        .unwrap();
        assert_eq!(drawn_wide(&image), wide, "item{id}");
    }
    let screen = Vec2::new(1280., 720.);
    for n in 2..=20 {
        let centers: Vec<_> = (0..n)
            .map(|i| option_position(Vec2::ZERO, Vec2::splat(44.), screen, i, n) + Vec2::splat(22.))
            .collect();
        for i in 0..n {
            assert!(centers[i].distance(centers[(i + 1) % n]) >= 53.999);
            assert!(centers[i].min_element() >= 22.);
        }
    }
}

mod udp;
