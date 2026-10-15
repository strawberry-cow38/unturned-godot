//! Original equipable prefab hierarchy and LOD membership for animated held food.
use crate::{
    content::{ContentCache, ContentError},
    rig::RigPlayback,
};
use bevy::{camera::visibility::RenderLayers, prelude::*};
use std::{collections::HashMap, path::Path};

#[derive(Debug)]
pub struct Part {
    name: String,
    path: String,
    parent: Option<usize>,
    transform: Transform,
    mesh: Option<String>,
    texture: Option<String>,
}
fn floats<const N: usize>(text: &str) -> Result<[f32; N], ContentError> {
    let values = text
        .split_whitespace()
        .map(str::parse::<f32>)
        .collect::<Result<Vec<_>, _>>()
        .map_err(|_| ContentError("invalid held-part transform".into()))?;
    if values.iter().any(|v| !v.is_finite()) {
        return Err(ContentError("non-finite held-part transform".into()));
    }
    values
        .try_into()
        .map_err(|_| ContentError("invalid held-part transform length".into()))
}
pub fn parse(text: &str) -> Result<Vec<Part>, ContentError> {
    let mut parts: Vec<Part> = Vec::new();
    let mut names = HashMap::new();
    for row in text.lines().filter(|r| !r.trim().is_empty()) {
        let f: Vec<_> = row.split('\t').map(str::trim).collect();
        if f.len() < 8 {
            return Err(ContentError("held-part row needs eight fields".into()));
        }
        let parent = if f[5] == "-" {
            None
        } else {
            Some(
                *names
                    .get(f[5])
                    .ok_or_else(|| ContentError(format!("{} has missing parent {}", f[0], f[5])))?,
            )
        };
        let path = parent.map_or_else(
            || f[0].to_owned(),
            |p: usize| format!("{}/{}", parts[p].path, f[0]),
        );
        let rotation = Quat::from_array(floats(f[3])?);
        if rotation.length_squared() < 0.00001 {
            return Err(ContentError("invalid held-part quaternion".into()));
        }
        let part = Part {
            name: f[0].into(),
            path,
            parent,
            transform: Transform {
                translation: Vec3::from_array(floats(f[2])?),
                rotation: rotation.normalize(),
                scale: Vec3::from_array(floats(f[4])?),
            },
            mesh: (f[1] != "-" && f[6] == "1").then(|| f[1].into()),
            texture: (f[7] != "-").then(|| f[7].into()),
        };
        names.insert(f[0].to_owned(), parts.len());
        parts.push(part);
    }
    Ok(parts)
}
#[allow(clippy::too_many_arguments)]
pub fn spawn(
    source: &str,
    root: &Path,
    parent: Entity,
    default_material: Handle<StandardMaterial>,
    playback: &mut RigPlayback,
    commands: &mut Commands,
    cache: &mut ContentCache,
    meshes: &mut Assets<Mesh>,
    materials: &mut Assets<StandardMaterial>,
    assets: &AssetServer,
) -> Result<usize, ContentError> {
    let parts = parse(source)?;
    let mut entities = Vec::new();
    let mut drawn = 0;
    for part in parts {
        let parent = part.parent.map_or(parent, |p| entities[p]);
        let node = commands
            .spawn((
                Name::new(part.name),
                part.transform,
                Visibility::Inherited,
                ChildOf(parent),
            ))
            .id();
        playback.bind_item_track(part.path, node);
        if let Some(mesh) = part.mesh {
            let (mesh, _) = cache.load_mesh(&root.join(mesh), meshes)?;
            let material = if let Some(texture) = &part.texture {
                cache.material(Some(texture), true, assets, materials)
            } else {
                default_material.clone()
            };
            commands.spawn((
                Mesh3d(mesh),
                MeshMaterial3d(material),
                RenderLayers::layer(super::LAYER),
                ChildOf(node),
            ));
            drawn += 1;
        }
        entities.push(node);
    }
    Ok(drawn)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn original_food_preserves_lod_selection_filling_materials_and_animation_paths() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../game/content");
        let beans =
            parse(&std::fs::read_to_string(root.join("canned_beans_parts.tsv")).unwrap()).unwrap();
        assert!(
            beans
                .iter()
                .find(|p| p.name == "Model_0")
                .unwrap()
                .mesh
                .is_some()
        );
        assert!(
            beans
                .iter()
                .find(|p| p.name == "Model_1")
                .unwrap()
                .mesh
                .is_none()
        );
        assert!(
            beans
                .iter()
                .find(|p| p.name == "Model_2")
                .unwrap()
                .mesh
                .is_some()
        );
        let chips =
            parse(&std::fs::read_to_string(root.join("bag_chips_parts.tsv")).unwrap()).unwrap();
        assert!(
            chips
                .iter()
                .find(|p| p.name == "Model_1")
                .unwrap()
                .mesh
                .is_none()
        );
        let filling = chips.iter().find(|p| p.name == "Bone_3").unwrap();
        assert!(filling.path.starts_with("Item_Root/"));
        assert_ne!(filling.texture.as_deref(), Some("bag_chips_albedo.png"));
        assert!(parse("Bone_0\t-\t0 0 0\t0 0 0 1\t1 1 1\tmissing\t0\t-").is_err());
    }
}
