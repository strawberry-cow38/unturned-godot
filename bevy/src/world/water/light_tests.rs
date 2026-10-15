use super::*;
use bevy::{
    camera::{
        Viewport,
        primitives::Frustum,
        visibility::{VisibilityPlugin, VisibleEntities},
    },
    light::cluster::{
        ClusterableObjects, Clusters, GlobalClusterGpuSettings, GlobalClusterSettings,
    },
    mesh::skinning::SkinnedMeshInverseBindposes,
};
use std::collections::HashSet;

fn app() -> App {
    let mut app = App::new();
    app.add_plugins((MinimalPlugins, AssetPlugin::default(), TransformPlugin))
        .init_asset::<Mesh>()
        .init_asset::<Image>()
        .init_asset::<SkinnedMeshInverseBindposes>()
        .add_plugins((
            VisibilityPlugin,
            bevy::gizmos::GizmoPlugin,
            bevy::light::LightPlugin,
        ))
        .insert_resource(GlobalClusterSettings {
            supports_storage_buffers: true,
            clustered_decals_are_usable: false,
            gpu_clustering: Some(GlobalClusterGpuSettings {
                initial_z_slice_list_capacity: 4096,
                initial_index_list_capacity: 16384,
            }),
            max_uniform_buffer_clusterable_objects: 256,
            view_cluster_bindings_max_indices: 65536,
        })
        .add_systems(Startup, crate::hdr::layers::configure);
    install_lights(&mut app);
    app
}
fn camera(app: &mut App, layer: usize) -> Entity {
    let mut camera = Camera {
        viewport: Some(Viewport {
            physical_size: UVec2::new(320, 180),
            ..default()
        }),
        ..default()
    };
    camera.computed.clip_from_view =
        Mat4::perspective_infinite_reverse_rh(60_f32.to_radians(), 16. / 9., 0.1);
    app.world_mut()
        .spawn((
            camera,
            Clusters::default(),
            Frustum::default(),
            VisibleEntities::default(),
            Transform::IDENTITY,
            RenderLayers::layer(layer),
        ))
        .id()
}
fn clustered(app: &App, camera: Entity) -> HashSet<Entity> {
    let ClusterableObjects::Cpu(clusters) = &app
        .world()
        .get::<Clusters>(camera)
        .unwrap()
        .clusterable_objects
    else {
        panic!("separate viewport lights require Bevy's layer-aware CPU assignment")
    };
    clusters.iter().flat_map(|c| c.iter().copied()).collect()
}

#[test]
fn real_clusters_isolate_world_viewmodel_reflection_and_hide_copies_on_source_off() {
    let mut app = app();
    let main = camera(&mut app, 0);
    let vm = camera(&mut app, 1);
    let reflection = camera(&mut app, REFLECTION_LAYER);
    let source = app
        .world_mut()
        .spawn((
            SpotLight {
                range: 12.,
                shadow_maps_enabled: false,
                ..default()
            },
            Transform::from_xyz(0., 0., -2.),
            Visibility::Visible,
        ))
        .id();
    let fill = app
        .world_mut()
        .spawn((
            PointLight {
                range: 8.,
                shadow_maps_enabled: false,
                ..default()
            },
            Transform::from_xyz(0., 0., -2.),
            RenderLayers::layer(1),
        ))
        .id();
    app.update();
    let copy = app
        .world_mut()
        .query_filtered::<Entity, With<ReflectionLight>>()
        .single(app.world())
        .unwrap();
    assert_eq!(clustered(&app, main), HashSet::from([source]));
    assert_eq!(clustered(&app, vm), HashSet::from([fill]));
    assert_eq!(clustered(&app, reflection), HashSet::from([copy]));
    assert!(
        !app.world()
            .get::<SpotLight>(copy)
            .unwrap()
            .shadow_maps_enabled
    );
    for on in [false, true, false] {
        *app.world_mut().get_mut::<Visibility>(source).unwrap() = if on {
            Visibility::Visible
        } else {
            Visibility::Hidden
        };
        app.update();
        assert_eq!(
            app.world().get::<InheritedVisibility>(copy).unwrap().get(),
            on
        );
        assert_eq!(app.world().get::<ViewVisibility>(copy).unwrap().get(), on);
        assert_eq!(
            clustered(&app, main),
            if on {
                HashSet::from([source])
            } else {
                HashSet::new()
            }
        );
        assert_eq!(
            clustered(&app, reflection),
            if on {
                HashSet::from([copy])
            } else {
                HashSet::new()
            }
        );
        assert_eq!(clustered(&app, vm), HashSet::from([fill]));
    }
    // A source changing to a private viewport must retire its old ocean copy.
    app.world_mut()
        .entity_mut(source)
        .insert((RenderLayers::layer(1), Visibility::Visible));
    app.update();
    assert!(app.world().get_entity(copy).is_err());
    assert!(clustered(&app, main).is_empty());
    assert!(clustered(&app, reflection).is_empty());
    assert_eq!(clustered(&app, vm), HashSet::from([source, fill]));
}

#[test]
fn reflection_obeys_hidden_ancestor_even_for_explicitly_visible_light() {
    let mut app = app();
    camera(&mut app, REFLECTION_LAYER);
    let parent = app
        .world_mut()
        .spawn((Transform::IDENTITY, Visibility::Hidden))
        .id();
    let source = app
        .world_mut()
        .spawn((
            PointLight {
                shadow_maps_enabled: false,
                ..default()
            },
            Transform::from_xyz(0., 0., -2.),
            Visibility::Visible,
            ChildOf(parent),
        ))
        .id();
    app.update();
    let copy = app
        .world_mut()
        .query_filtered::<Entity, With<ReflectionLight>>()
        .single(app.world())
        .unwrap();
    assert_eq!(
        app.world().get::<Visibility>(copy),
        Some(&Visibility::Hidden)
    );
    assert!(!app.world().get::<ViewVisibility>(copy).unwrap().get());
    *app.world_mut().get_mut::<Visibility>(parent).unwrap() = Visibility::Visible;
    app.update();
    assert_eq!(
        app.world().get::<Visibility>(copy),
        Some(&Visibility::Visible)
    );
    app.world_mut().despawn(source);
    app.update();
    assert!(app.world().get_entity(copy).is_err());
}
