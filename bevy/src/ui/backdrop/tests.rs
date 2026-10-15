use super::*;
#[test]
fn routes_only_requested_roots_and_recovers_after_camera_replacement() {
    let mut app = App::new();
    app.add_systems(Startup, spawn_camera)
        .add_systems(PostUpdate, route);
    let hud = app.world_mut().spawn(Node::default()).id();
    let modal = app
        .world_mut()
        .spawn((Node::default(), Backdrop::ammo_pie(0.7)))
        .id();
    app.update();
    let camera = app.world().get::<UiTargetCamera>(modal).unwrap().0;
    assert_eq!(
        app.world().get::<Settings>(camera).unwrap().parameters.x,
        0.7
    );
    assert_eq!(
        app.world().get::<Camera>(camera).unwrap().order,
        super::super::layers::MODAL_ORDER
    );
    assert!(app.world().get::<UiTargetCamera>(hud).is_none());
    app.world_mut().entity_mut(camera).despawn();
    app.update();
    let replacement = app.world().get::<UiTargetCamera>(modal).unwrap().0;
    assert_ne!(replacement, camera);
    assert!(app.world().get::<ModalCamera>(replacement).is_some());
    app.world_mut().entity_mut(modal).remove::<Backdrop>();
    app.update();
    assert!(app.world().get::<UiTargetCamera>(modal).is_none());
    assert_eq!(
        app.world()
            .get::<Settings>(replacement)
            .unwrap()
            .parameters
            .x,
        0.
    );
    app.world_mut()
        .entity_mut(modal)
        .insert(Backdrop::ammo_pie(1.));
    app.update();
    app.world_mut().entity_mut(modal).despawn();
    app.update();
    assert_eq!(
        app.world()
            .get::<Settings>(replacement)
            .unwrap()
            .parameters
            .x,
        0.
    );
}
#[test]
fn backdrop_uses_source_lod_spread_and_eased_fade_without_a_second_clock() {
    assert_eq!(Backdrop::ammo_pie(f32::NAN).fade, 0.);
    assert_eq!(Backdrop::ammo_pie(-1.).fade, 0.);
    assert_eq!(Backdrop::ammo_pie(2.).fade, 1.);
    let mut app = App::new();
    app.add_systems(PostUpdate, route);
    app.world_mut().spawn(Backdrop::ammo_pie(0.875));
    app.update();
    let settings = app
        .world_mut()
        .query_filtered::<&Settings, With<ModalCamera>>()
        .single(app.world())
        .unwrap();
    assert_eq!(settings.parameters, Vec4::new(0.875, 1.8, 3., 0.));
    app.update();
    let settings = app
        .world_mut()
        .query_filtered::<&Settings, With<ModalCamera>>()
        .single(app.world())
        .unwrap();
    assert_eq!(settings.parameters.x, 0.875);
}

mod visual;

#[test]
fn dashboard_vitals_stay_above_frost_and_below_a_higher_modal() {
    let mut app = App::new();
    app.add_systems(PostUpdate, route);
    let hud = app.world_mut().spawn(Node::default()).id();
    let vitals = app
        .world_mut()
        .spawn((Node::default(), AboveDashboard))
        .id();
    let panel = app.world_mut().spawn(Backdrop::dashboard(1.)).id();
    let modal = app.world_mut().spawn(Backdrop::ammo_pie(0.75)).id();
    app.update();
    let dashboard = app.world().get::<UiTargetCamera>(panel).unwrap().0;
    let higher = app.world().get::<UiTargetCamera>(modal).unwrap().0;
    assert_eq!(
        app.world().get::<UiTargetCamera>(vitals).unwrap().0,
        dashboard
    );
    assert!(app.world().get::<UiTargetCamera>(hud).is_none());
    assert!(
        app.world().get::<Camera>(dashboard).unwrap().order
            < app.world().get::<Camera>(higher).unwrap().order
    );
    assert_eq!(
        app.world().get::<Settings>(dashboard).unwrap().parameters,
        Vec4::new(1., 1.5, 3., 1.)
    );
    assert_eq!(
        app.world().get::<Settings>(higher).unwrap().parameters,
        Vec4::new(0.75, 1.8, 3., 0.)
    );
    // Camera replacement must preserve the same source layering before layout.
    app.world_mut().entity_mut(dashboard).despawn();
    app.update();
    let replacement = app.world().get::<UiTargetCamera>(panel).unwrap().0;
    assert_ne!(replacement, dashboard);
    assert_eq!(
        app.world().get::<UiTargetCamera>(vitals).unwrap().0,
        replacement
    );
    app.world_mut().entity_mut(panel).despawn();
    app.update();
    assert!(
        app.world().get::<UiTargetCamera>(vitals).is_none(),
        "layer60 now blurs the ordinary vitals"
    );
    assert_eq!(
        app.world()
            .get::<Settings>(replacement)
            .unwrap()
            .parameters
            .x,
        0.
    );
}

#[test]
fn production_pause_and_vitals_do_not_use_dashboard_frost() {
    use crate::ui::{
        OverlayRoot, Screen, Settings as UiSettings, UiModel, UiState, rebuild_overlay,
    };
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, bevy::asset::AssetPlugin::default()))
        .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
            std::time::Duration::from_millis(100),
        ))
        .init_asset::<Image>()
        .init_resource::<UiState>()
        .init_resource::<UiModel>()
        .init_resource::<UiSettings>()
        .init_resource::<crate::profile::LocalProfile>()
        .init_resource::<crate::entities::ItemCatalog>()
        .add_plugins((
            crate::ui::vitals::VitalsPlugin,
            crate::ui::swoop::SwoopPlugin,
        ))
        .add_systems(Update, rebuild_overlay)
        .add_systems(PostUpdate, route.in_set(BackdropRouting));
    app.world_mut().resource_mut::<UiModel>().connected = true;
    // Inventory and Crafting now assert their metadata in their real renderers.
    for screen in [Screen::Paused] {
        app.world_mut().resource_mut::<UiState>().screen = screen;
        app.update();
        let world = app.world_mut();
        let root = world
            .query_filtered::<Entity, With<OverlayRoot>>()
            .single(world)
            .unwrap();
        let dashboard = matches!(screen, Screen::Inventory | Screen::Crafting);
        assert_eq!(world.get::<Backdrop>(root).is_some(), dashboard);
        let vitals = world
            .query_filtered::<Entity, With<AboveDashboard>>()
            .single(world)
            .unwrap();
        if dashboard {
            assert_eq!(world.get::<BackgroundColor>(root).unwrap().0.alpha(), 0.);
            assert_eq!(
                world.get::<UiTargetCamera>(root).unwrap().0,
                world.get::<UiTargetCamera>(vitals).unwrap().0
            );
        } else {
            assert!(world.get::<UiTargetCamera>(vitals).is_none());
            assert!(world.get::<UiTargetCamera>(root).is_none());
        }
    }
}
