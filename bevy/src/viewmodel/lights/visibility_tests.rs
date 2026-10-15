use super::super::*;
use bevy::camera::{
    primitives::{Frustum, Sphere},
    visibility::{InheritedVisibility, ViewVisibility, VisibilityPlugin, VisibleEntities},
};
use bevy::mesh::skinning::SkinnedMeshInverseBindposes;
use serde_json::json;

#[test]
fn light_switch_updates_extraction_visibility_in_the_same_render_frame() {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        VisibilityPlugin,
        RapierPhysicsPlugin::<NoUserData>::default().in_fixed_schedule(),
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<AudioSource>()
    .init_asset::<SkinnedMeshInverseBindposes>()
    .init_resource::<ItemCatalog>()
    .init_resource::<ViewmodelState>()
    .init_resource::<WorldSessionEpoch>()
    .init_resource::<Settings>()
    .init_resource::<WorldConfig>()
    .init_resource::<ViewmodelStatus>()
    .init_resource::<ButtonInput<KeyCode>>()
    .init_resource::<ButtonInput<MouseButton>>()
    .insert_resource(UiState {
        screen: Screen::Playing,
        ..default()
    })
    .insert_resource(GameSnapshot(
        json!({"connected":true,"player":{"alive":true},"inventory":{"worn":{"glasses":1199}}}),
    ))
    .add_plugins((
        bevy::gizmos::GizmoPlugin,
        bevy::light::LightPlugin,
        LightPlugin,
    ));
    let camera = app
        .world_mut()
        .spawn((
            PlayerCamera,
            Transform::from_xyz(0., 1., 0.),
            Projection::Perspective(PerspectiveProjection::default()),
            Camera::default(),
            Frustum::default(),
            VisibleEntities::default(),
        ))
        .id();
    app.world_mut().resource_mut::<Switches>().head = true;
    app.update();
    let lamp = app.world().resource::<Visuals>().lamps[2].unwrap();
    assert!(
        app.world().get::<InheritedVisibility>(lamp).unwrap().get(),
        "newly enabled lamp must reach visibility propagation before extraction"
    );
    assert!(
        app.world().get::<ViewVisibility>(lamp).unwrap().get(),
        "new lamp must enter the view in its creation frame"
    );
    let sphere = app
        .world()
        .get::<Sphere>(lamp)
        .expect("new light requires culling bounds in its creation frame");
    assert_eq!(Vec3::from(sphere.center), Vec3::new(0., 1., 0.));
    app.world_mut()
        .get_mut::<Transform>(camera)
        .unwrap()
        .translation = Vec3::new(7., 3., -2.);
    for on in [false, true, false, true, false] {
        app.world_mut().resource_mut::<Switches>().head = on;
        app.update();
        assert_eq!(
            Vec3::from(app.world().get::<Sphere>(lamp).unwrap().center),
            Vec3::new(7., 3., -2.),
            "culling sphere must follow the current camera pose"
        );
        assert_eq!(
            app.world().get::<Visibility>(lamp),
            Some(if on {
                &Visibility::Visible
            } else {
                &Visibility::Hidden
            })
        );
        assert_eq!(
            app.world().get::<InheritedVisibility>(lamp).unwrap().get(),
            on,
            "propagated switch must match intent in this frame"
        );
        assert_eq!(
            app.world().get::<ViewVisibility>(lamp).unwrap().get(),
            on,
            "PBR extract_lights consumes this derived visibility, not Visibility directly"
        );
    }
}
