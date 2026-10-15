use super::*;
use crate::{
    avatar::remotes::held::Held,
    character::{GearItem, Wardrobe},
};
use bevy::{
    asset::RenderAssetUsages,
    image::{CompressedImageFormats, ImageSampler, ImageType},
};

fn load_texture(app: &mut App, handle: &Handle<Image>) {
    let path = app
        .world()
        .resource::<AssetServer>()
        .get_path(handle.id())
        .unwrap();
    let bytes = std::fs::read(WorldConfig::default().content_root.join(path.path())).unwrap();
    let image = Image::from_buffer(
        &bytes,
        ImageType::Extension("png"),
        CompressedImageFormats::NONE,
        true,
        ImageSampler::nearest(),
        RenderAssetUsages::default(),
    )
    .unwrap();
    app.world_mut()
        .resource_mut::<Assets<Image>>()
        .insert(handle.id(), image)
        .unwrap();
}
fn material(app: &App, entity: Entity) -> &StandardMaterial {
    let handle = &app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(entity)
        .unwrap()
        .0;
    app.world()
        .resource::<Assets<StandardMaterial>>()
        .get(handle)
        .unwrap()
}

#[test]
fn accepted_peer_lamps_use_original_lens_masks_without_redressing_or_local_switches() {
    let mut app = app();
    app.insert_resource(source_catalog());
    let root = body(&app);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["heldItemId"] = json!(276);
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["lights"] =
        json!({"worn":true,"held":true});
    app.update();
    let torch = app.world().get::<Held>(root).unwrap().root.unwrap();
    let original_texture = material(&app, torch).base_color_texture.clone().unwrap();
    assert_eq!(
        material(&app, torch).emissive.red,
        0.,
        "pending mask cannot illuminate the whole torch"
    );
    load_texture(&mut app, &original_texture);
    let wardrobe = Wardrobe::load(&WorldConfig::default().content_root);
    let image: Handle<Image> = app
        .world()
        .resource::<AssetServer>()
        .load(wardrobe.entries[&334].albedo.clone());
    load_texture(&mut app, &image);
    let mat = app
        .world_mut()
        .resource_mut::<Assets<StandardMaterial>>()
        .add(StandardMaterial {
            base_color_texture: Some(image),
            ..default()
        });
    let skull = app.world().get::<RigPlayback>(root).unwrap().bones["Skull"];
    let goggles = app
        .world_mut()
        .spawn((
            GearItem(334),
            MeshMaterial3d(mat),
            Transform::default(),
            ChildOf(skull),
        ))
        .id();
    app.update();
    assert_eq!(material(&app, torch).emissive.red, 3.6);
    let expected_nvg = std::env::var("UG_NVGLOW")
        .ok()
        .and_then(|s| s.parse::<f32>().ok())
        .unwrap_or(1.4);
    assert_eq!(material(&app, goggles).emissive.red, expected_nvg);
    let mask = material(&app, torch).emissive_texture.clone().unwrap();
    let pixels = app
        .world()
        .resource::<Assets<Image>>()
        .get(&mask)
        .unwrap()
        .data
        .as_ref()
        .unwrap();
    let lit = pixels
        .chunks_exact(4)
        .filter(|p| p[..3].iter().any(|v| *v != 0))
        .count();
    assert_eq!(
        lit, 812,
        "original 784 bulb texels plus28 near twins; housing stays black"
    );
    let material_id = app
        .world()
        .get::<MeshMaterial3d<StandardMaterial>>(torch)
        .unwrap()
        .0
        .id();
    app.world_mut().resource_mut::<GameSnapshot>().0["players"][1]["lights"] =
        json!({"worn":false,"held":false});
    app.update();
    assert_eq!(material(&app, goggles).emissive.red, 0.);
    assert_eq!(material(&app, torch).emissive.red, 0.);
    assert_eq!(app.world().get::<Held>(root).unwrap().root, Some(torch));
    assert_eq!(
        app.world()
            .get::<MeshMaterial3d<StandardMaterial>>(torch)
            .unwrap()
            .0
            .id(),
        material_id
    );
    assert_eq!(material(&app, torch).emissive_texture, Some(mask));
    assert_eq!(
        app.world_mut()
            .query::<&SpotLight>()
            .iter(app.world())
            .count(),
        0,
        "source puppets add no spotlights"
    );
}
