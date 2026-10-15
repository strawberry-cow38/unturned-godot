//! RemotePlayers.SetGlassesGlow/SetMeleeGlow: accepted emissive flags only.
//! Source puppets have glowing lenses/bulbs, not extra local-owner spotlights.
use super::*;
use crate::{
    character::GearItem,
    viewmodel::lights::{EmissionMasks, apply_surface_emission},
};

pub(super) fn update(
    snapshot: Res<GameSnapshot>,
    players: Query<(Entity, &RemotePlayer, &held::Held)>,
    parents: Query<&ChildOf>,
    gear: Query<(Entity, &GearItem, &MeshMaterial3d<StandardMaterial>)>,
    held_materials: Query<&MeshMaterial3d<StandardMaterial>>,
    mut masks: ResMut<EmissionMasks>,
    mut materials: ResMut<Assets<StandardMaterial>>,
    mut images: ResMut<Assets<Image>>,
) {
    let rows: HashMap<_, _> = replicas(&snapshot.0).collect();
    for (entity, item, handle) in &gear {
        if !matches!(item.0, 1199 | 334 | 1044) {
            continue;
        }
        let mut ancestor = entity;
        for _ in 0..32 {
            if let Ok((_, player, _)) = players.get(ancestor) {
                if let Some(row) = rows.get(&player.id) {
                    if let Some(mut material) = materials.get_mut(&handle.0) {
                        apply_surface_emission(
                            item.0 as u16,
                            row["lights"]["worn"].as_bool().unwrap_or(false),
                            &mut material,
                            &mut masks,
                            &mut images,
                        );
                    }
                }
                break;
            }
            let Ok(parent) = parents.get(ancestor) else {
                break;
            };
            ancestor = parent.parent();
        }
    }
    for (_, player, held) in &players {
        if held.gun {
            continue;
        }
        let Some(row) = rows.get(&player.id) else {
            continue;
        };
        let Some(root) = held.root else { continue };
        let Ok(handle) = held_materials.get(root) else {
            continue;
        };
        if let Some(mut material) = materials.get_mut(&handle.0) {
            apply_surface_emission(
                held.id,
                row["lights"]["held"].as_bool().unwrap_or(false),
                &mut material,
                &mut masks,
                &mut images,
            );
        }
    }
}
