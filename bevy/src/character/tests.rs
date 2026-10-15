use super::*;
use crate::rig::{RigFixedAnimation, RigLibrary, RigPlayback, RigPlugin, spawn_rig};
use bevy::mesh::skinning::SkinnedMeshInverseBindposes;

fn setup(
    mut commands: Commands,
    mut library: ResMut<RigLibrary>,
    mut meshes: ResMut<Assets<Mesh>>,
    mut binds: ResMut<Assets<SkinnedMeshInverseBindposes>>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    config: Res<WorldConfig>,
) {
    let rig = spawn_rig(
        &mut commands,
        &mut library,
        &mut meshes,
        &mut binds,
        &config.content_root.join("rig.json"),
        materials.add(StandardMaterial::default()),
        Transform::from_rotation(Quat::from_rotation_y(1.2)),
        false,
        "Idle_0",
    )
    .unwrap();
    commands.entity(rig).insert((
        RigFixedAnimation,
        Appearance {
            shirt: 24,
            pants: 23,
            gear: vec![26, 27, 9],
            face: Some(14),
            ..default()
        },
    ));
}
fn app() -> App {
    let mut app = App::new();
    app.add_plugins((
        MinimalPlugins,
        AssetPlugin::default(),
        TransformPlugin,
        RigPlugin,
    ))
    .init_asset::<Mesh>()
    .init_asset::<Image>()
    .init_asset::<StandardMaterial>()
    .init_asset::<SkinnedMeshInverseBindposes>()
    .init_asset::<bevy::shader::Shader>()
    .init_resource::<ContentCache>()
    .init_resource::<WorldConfig>()
    .add_plugins(CharacterPlugin)
    .add_systems(Startup, setup)
    .insert_resource(Time::<Fixed>::from_hz(50.))
    .insert_resource(bevy::time::TimeUpdateStrategy::ManualDuration(
        std::time::Duration::from_millis(10),
    ));
    app.update();
    app.update();
    app
}
#[test]
fn original_wardrobe_composites_metallic_layers_attaches_gear_and_replaces_cleanly() {
    let mut app = app();
    let world = app.world_mut();
    let (entity, skin, parts, skull, spine) = world
        .query::<(Entity, &RigSkin, &Dressed, &RigPlayback)>()
        .single(world)
        .map(|(e, s, d, r)| (e, s.0, d.parts.clone(), r.bones["Skull"], r.bones["Spine"]))
        .unwrap();
    assert_eq!(parts.len(), 4, "face, two skull accessories and backpack");
    let handle = &world
        .get::<MeshMaterial3d<ClothesMaterial>>(skin)
        .unwrap()
        .0;
    let material = world
        .resource::<Assets<ClothesMaterial>>()
        .get(handle)
        .unwrap();
    assert_eq!(
        material.extension.flags.x, 0b101101,
        "real gold shirt/pants each have albedo+metallic, no emission"
    );
    assert_eq!(
        material.base.cull_mode,
        Some(bevy::render::render_resource::Face::Back)
    );
    assert!(
        world
            .get::<MeshMaterial3d<StandardMaterial>>(skin)
            .is_none()
    );
    let mut saw_mask = false;
    for p in &parts {
        let name = world.get::<Name>(*p).unwrap().as_str();
        let parent = world.get::<ChildOf>(*p).unwrap().parent();
        assert_eq!(
            parent,
            if name.starts_with("backpack") {
                spine
            } else {
                skull
            }
        );
        let material = world
            .resource::<Assets<StandardMaterial>>()
            .get(&world.get::<MeshMaterial3d<StandardMaterial>>(*p).unwrap().0)
            .unwrap();
        if name.starts_with("glasses") {
            assert_eq!(
                material.base_color,
                Color::Srgba(Srgba::hex("#d2bf22").unwrap())
            );
            assert!(material.base_color_texture.is_none());
            saw_mask = true;
        }
        if name == "Original skull face" {
            assert!(
                material.emissive_texture.is_some(),
                "face14 source mask must emit only at marked texels"
            );
            assert_eq!(
                world.get::<Transform>(*p).unwrap().translation,
                Vec3::new(-0.43, 0., -0.212)
            );
        }
    }
    assert!(saw_mask);
    world.entity_mut(entity).insert(Appearance::default());
    app.update();
    for old in parts {
        assert!(app.world().get_entity(old).is_err());
    }
    assert!(app.world().get::<Dressed>(entity).unwrap().parts.is_empty());
    let skin = app.world().get::<RigSkin>(entity).unwrap().0;
    let handle = &app
        .world()
        .get::<MeshMaterial3d<ClothesMaterial>>(skin)
        .unwrap()
        .0;
    assert_eq!(
        app.world()
            .resource::<Assets<ClothesMaterial>>()
            .get(handle)
            .unwrap()
            .extension
            .flags
            .x,
        0
    );
}
#[test]
fn physics_animation_advances_once_at_fifty_hz_and_not_on_extra_render_updates() {
    let mut app = app();
    let entity = app
        .world_mut()
        .query_filtered::<Entity, With<RigPlayback>>()
        .single(app.world())
        .unwrap();
    let initial = app.world().get::<RigPlayback>(entity).unwrap().time;
    app.update();
    assert!((app.world().get::<RigPlayback>(entity).unwrap().time - initial - 0.02).abs() < 1e-6);
    app.update();
    assert!((app.world().get::<RigPlayback>(entity).unwrap().time - initial - 0.02).abs() < 1e-6);
}
