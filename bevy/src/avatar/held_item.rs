//! Shared RiggedCharacter.AttachGun/AttachMelee geometry for replica bodies and
//! the inventory portrait. Callers supply accepted equipment and a view layer;
//! this builder has no inventory, combat, physics or input ownership.
use crate::{
    content::ContentCache,
    entities::ItemCatalog,
    rig::RigPlayback,
    viewmodel::{GunVisual, attachments},
    world::WorldConfig,
};
use bevy::{camera::visibility::RenderLayers, prelude::*};
use serde_json::Value;
use std::collections::HashMap;

#[derive(Resource, Default)]
pub(crate) struct Library(Option<HashMap<String, GunVisual>>);

#[derive(Component, Default)]
pub(crate) struct Held {
    pub id: u16,
    pub asset: Option<String>,
    pub gun: bool,
    pub root: Option<Entity>,
    pub muzzle: Option<Vec3>,
    signature: Option<Value>,
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn sync(
    commands: &mut Commands,
    held: &mut Held,
    rig: &RigPlayback,
    id: u16,
    installed: &Value,
    layers: &RenderLayers,
    label: &str,
    catalog: &ItemCatalog,
    config: &WorldConfig,
    assets: &AssetServer,
    library: &mut Library,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
) {
    let signature = serde_json::json!([id, *installed]);
    if held.signature.as_ref() == Some(&signature) {
        return;
    }
    if let Some(root) = held.root.take() {
        commands.entity(root).try_despawn();
    }
    *held = Held { id, ..default() };
    if id == 0 {
        held.signature = Some(signature);
        return;
    }
    // Replica arrival and catalog loading are independently scheduled. An
    // unresolved item is pending, not a successfully dressed empty hand.
    let Some(item) = catalog.items.get(&id) else {
        return;
    };
    let gun = item.item_type == "gun";
    if !gun && item.melee.is_null() {
        held.signature = Some(signature);
        return;
    }
    let Some(asset) = item.asset.as_deref().filter(|a| !a.is_empty()) else {
        held.signature = Some(signature);
        return;
    };
    held.asset = Some(asset.into());
    held.gun = gun;
    let Some(hook) = rig
        .bones
        .get(if gun && item.gun["leftHook"].as_bool().unwrap_or(false) {
            "Left_Hook"
        } else {
            "Right_Hook"
        })
        .or_else(|| rig.bones.get("Right_Hand"))
    else {
        return;
    };
    if library.0.is_none() {
        library.0 = crate::viewmodel::load_gun_visuals(&config.content_root).ok();
    }
    let Some(visuals) = &library.0 else { return };
    let visual = gun.then(|| visuals.get(asset)).flatten();
    let (mesh, texture) = if gun {
        let Some(visual) = visual else { return };
        (visual.mesh.clone(), visual.texture.clone())
    } else {
        (format!("{asset}.txt"), format!("{asset}_albedo.png"))
    };
    let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(&mesh), meshes) else {
        return;
    };
    let texture = config
        .content_root
        .join(&texture)
        .is_file()
        .then_some(texture);
    let cached = cache.material(texture.as_deref(), false, assets, materials);
    let mut material = materials.get(&cached).cloned().unwrap_or_default();
    // The source explicitly disables culling for the ripped held meshes.
    material.cull_mode = None;
    let material = materials.add(material);
    let root = commands
        .spawn((
            Name::new(format!("{label} held item {id}")),
            Mesh3d(mesh),
            MeshMaterial3d(material),
            Transform::from_rotation(Quat::from_rotation_z(std::f32::consts::FRAC_PI_2)),
            layers.clone(),
            ChildOf(*hook),
        ))
        .id();
    held.root = Some(root);
    held.signature = Some(signature);
    if let Some(visual) = visual {
        held.muzzle = Some(visual.muzzle);
        for mut part in attachments::body_parts(visual, installed, catalog) {
            let Ok((mesh, _)) = cache.load_mesh(&config.content_root.join(&part.mesh), meshes)
            else {
                continue;
            };
            let mut scale = 1.;
            if part.id == 9400 && part.slot == "Barrel" {
                if let Ok((_, data)) =
                    cache.load_mesh(&config.content_root.join(&visual.mesh), meshes)
                {
                    (part.position, scale) = attachments::bottle_fit(visual, &data.positions);
                }
            }
            let material = materials.add(StandardMaterial {
                base_color: part.color,
                base_color_texture: part.texture.map(|p| assets.load(p)),
                perceptual_roughness: 1.,
                reflectance: 0.,
                cull_mode: None,
                ..default()
            });
            commands.spawn((
                Name::new(format!("{label} {} attachment {}", part.slot, part.id)),
                Mesh3d(mesh),
                MeshMaterial3d(material),
                Transform::from_translation(part.position).with_scale(Vec3::splat(scale)),
                layers.clone(),
                ChildOf(root),
            ));
        }
    }
}

/// Source ShowGunHold/ShowMeleeHold parks only the upper-body overlay at the
/// accepted weapon's Equip end. Locomotion and the portrait's Idle_Stand remain
/// independent, as do remote swing/gesture overrides.
pub(crate) fn ready_pose(rig: &RigPlayback, held: &Held) -> Option<(String, f32)> {
    let asset = held.asset.as_deref()?;
    let name = super::held::clip(
        rig,
        asset,
        "_Equip",
        if held.gun { "Gun_Equip" } else { "Melee_Equip" },
    );
    let clip = rig.data.anims.get(&name).filter(|clip| clip.length > 0.)?;
    Some((name, clip.length))
}
pub(crate) fn apply_ready(rig: &RigPlayback, held: &Held, transforms: &mut Query<&mut Transform>) {
    if let Some((name, time)) = ready_pose(rig, held) {
        super::held::apply_upper_body(rig, &rig.data.anims[&name], time, false, transforms);
    }
}
